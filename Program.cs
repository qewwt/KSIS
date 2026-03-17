using System;
using System.Diagnostics;             
using System.Net;                    
using System.Net.Sockets;              
using System.Runtime.InteropServices;  
using System.Text;                     

class MyTraceroute
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct IcmpHeader
    {
        public byte Type;
        public byte Code;
        public ushort Checksum;
        public ushort Id;
        public ushort Seq;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct IpHeader
    {
        public byte VerLen;
        public byte Tos;
        public ushort TotalLen;
        public ushort Id;
        public ushort Offset;
        public byte Ttl;
        public byte Protocol;
        public ushort Checksum;
        public uint SrcAddr;
        public uint DstAddr;
    }

    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: mytraceroute [-n] <host>");
            return;
        }

        bool resolve = true;
        string target = null;

        foreach (var a in args)
        {
            if (a == "-n")
                resolve = false;
            else
                target = a;
        }

        if (target == null)
        {
            Console.WriteLine("No target specified");
            return;
        }

        IPAddress destIp = null;
        if (!IPAddress.TryParse(target, out destIp))
        {
            var entry = Dns.GetHostEntry(target);
            foreach (var ip in entry.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    destIp = ip;
                    break;
                }
            }
            if (destIp == null)
            {
                Console.WriteLine("Unable to resolve host");
                return;
            }
        }

        const int maxHops = 32;
        const int probes = 3;
        const int timeoutMs = 3000;

        Console.WriteLine();
        Console.WriteLine($"traceroute to {target} ({destIp}) {maxHops} hops max");
        Console.WriteLine();

        Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
        sock.ReceiveTimeout = timeoutMs;

        ushort pid = (ushort)Process.GetCurrentProcess().Id;

        bool reached = false;
        byte[] recvBuf = new byte[512];

        for (int ttl = 1; ttl <= maxHops && !reached; ttl++)
        {
            Console.Write($"{ttl,2}  ");
            Console.Out.Flush();

            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

            bool gotAnyIp = false;
            IPAddress hopIp = null;

            for (int i = 0; i < probes; i++)
            {
                byte[] packet = BuildIcmpPacket(pid, (ushort)(ttl * 10 + i));

                FlushRecvBuffer(sock, recvBuf);

                var sw = Stopwatch.StartNew();

                EndPoint destEndPoint = new IPEndPoint(destIp, 0);
                try
                {
                    sock.SendTo(packet, destEndPoint);
                }
                catch
                {
                    Console.Write("*     ");
                    Console.Out.Flush();
                    continue;
                }

                bool gotReply = false;
                IPEndPoint fromEndPoint = null;

                while (true)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int ret;
                    try
                    {
                        ret = sock.ReceiveFrom(recvBuf, ref from);
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    if (ret <= 0) break;

                    fromEndPoint = (IPEndPoint)from;

                    if (ret < Marshal.SizeOf<IpHeader>() + Marshal.SizeOf<IcmpHeader>())
                        continue;

                    IpHeader ipHeader = BytesToStruct<IpHeader>(recvBuf, 0);
                    int ipHeaderLen = (ipHeader.VerLen & 0x0F) * 4;
                    if (ret < ipHeaderLen + Marshal.SizeOf<IcmpHeader>())
                        continue;

                    IcmpHeader icmpReply = BytesToStruct<IcmpHeader>(recvBuf, ipHeaderLen);

                    if (icmpReply.Type == 0)
                    {
                        if (icmpReply.Id != pid)
                            continue;

                        gotReply = true;
                        reached = true;
                        break;
                    }
                    else if (icmpReply.Type == 11)
                    {
                        int innerIpOffset = ipHeaderLen + Marshal.SizeOf<IcmpHeader>();
                        if (ret < innerIpOffset + Marshal.SizeOf<IpHeader>() + Marshal.SizeOf<IcmpHeader>())
                            continue;

                        IpHeader innerIp = BytesToStruct<IpHeader>(recvBuf, innerIpOffset);
                        int innerIpLen = (innerIp.VerLen & 0x0F) * 4;
                        int innerIcmpOffset = innerIpOffset + innerIpLen;
                        if (ret < innerIcmpOffset + Marshal.SizeOf<IcmpHeader>())
                            continue;

                        IcmpHeader innerIcmp = BytesToStruct<IcmpHeader>(recvBuf, innerIcmpOffset);
                        if (innerIcmp.Id != pid)
                            continue;

                        gotReply = true;
                        break;
                    }
                }

                if (gotReply)
                {
                    sw.Stop();
                    int rtt = (int)sw.Elapsed.TotalMilliseconds;
                    Console.Write($"{rtt} ms  ");
                    Console.Out.Flush();

                    if (!gotAnyIp && fromEndPoint != null)
                    {
                        hopIp = fromEndPoint.Address;
                        gotAnyIp = true;
                    }
                }
                else
                {
                    Console.Write("*     ");
                    Console.Out.Flush();
                }
            }

            if (gotAnyIp && hopIp != null)
            {
                if (resolve)
                {
                    try
                    {
                        var hostEntry = Dns.GetHostEntry(hopIp);
                        Console.Write($"{hostEntry.HostName} ({hopIp})");
                    }
                    catch
                    {
                        Console.Write($"{hopIp}");
                    }
                }
                else
                {
                    Console.Write($"{hopIp}");
                }
            }

            Console.WriteLine();
            Console.Out.Flush();

            if (reached)
                Console.WriteLine("\nServer was found!");
        }

        Console.WriteLine();
        sock.Close();
    }

    static byte[] BuildIcmpPacket(ushort id, ushort seq)
    {
        int headerSize = Marshal.SizeOf<IcmpHeader>();
        byte[] packet = new byte[headerSize + 32];

        IcmpHeader hdr = new IcmpHeader
        {
            Type = 8,
            Code = 0,
            Checksum = 0,
            Id = id,
            Seq = seq
        };

        StructToBytes(hdr, packet, 0);

        byte[] payload = Encoding.ASCII.GetBytes("traceroute");
        Array.Copy(payload, 0, packet, headerSize, payload.Length);

        ushort cs = CalcChecksum(packet, 0, packet.Length);
        hdr.Checksum = cs;
        StructToBytes(hdr, packet, 0);

        return packet;
    }

    static ushort CalcChecksum(byte[] buffer, int offset, int length)
    {
        uint sum = 0;
        int i = offset;

        while (length > 1)
        {
            ushort word = (ushort)((buffer[i] << 8) | buffer[i + 1]);
            sum += word;
            i += 2;
            length -= 2;
        }

        if (length > 0)
        {
            sum += (uint)(buffer[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    static void FlushRecvBuffer(Socket sock, byte[] buffer)
    {
        bool oldBlocking = sock.Blocking;
        sock.Blocking = false;
        try
        {
            while (true)
            {
                if (sock.Available == 0)
                    break;

                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int ret = sock.ReceiveFrom(buffer, ref from);
                if (ret <= 0) break;
            }
        }
        catch
        {
        }
        finally
        {
            sock.Blocking = oldBlocking;
        }
    }

    static void StructToBytes<T>(T str, byte[] buffer, int offset) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(str, ptr, false);
            Marshal.Copy(ptr, buffer, offset, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    static T BytesToStruct<T>(byte[] buffer, int offset) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        if (offset + size > buffer.Length)
            throw new ArgumentException("Buffer too small");

        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(buffer, offset, ptr, size);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
