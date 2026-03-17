using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

class MyTraceroute
{
    static byte ReadByte(byte[] buf, ref int offset) => buf[offset++];

    static ushort ReadUInt16(byte[] buf, ref int offset)
    {
        ushort val = (ushort)((buf[offset] << 8) | buf[offset + 1]);
        offset += 2;
        return val;
    }

    static uint ReadUInt32(byte[] buf, ref int offset)
    {
        uint val = ((uint)buf[offset] << 24) |
                   ((uint)buf[offset + 1] << 16) |
                   ((uint)buf[offset + 2] << 8) |
                   buf[offset + 3];
        offset += 4;
        return val;
    }

    static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: mytraceroute <IPv4>");
            return;
        }

        if (!IPAddress.TryParse(args[0], out IPAddress destIp) ||
            destIp.AddressFamily != AddressFamily.InterNetwork)
        {
            Console.WriteLine("Target must be a valid IPv4 address (e.g. 8.8.8.8)");
            return;
        }

        const int maxHops = 32;
        const int probes = 3;
        const int timeoutMs = 3000;

        Console.WriteLine();
        Console.WriteLine($"traceroute to {destIp} {maxHops} hops max");
        Console.WriteLine();

        using Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp)
        {
            ReceiveTimeout = timeoutMs
        };

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
                ushort seq = (ushort)(ttl * 10 + i);
                byte[] pkt = BuildIcmpPacket(pid, seq);

                FlushRecvBuffer(sock, recvBuf);

                var sw = Stopwatch.StartNew();

                EndPoint destEndPoint = new IPEndPoint(destIp, 0);
                try
                {
                    sock.SendTo(pkt, destEndPoint);
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

                    if (ret <= 0 || ret < 20 + 8)
                        break;

                    fromEndPoint = (IPEndPoint)from;

                    int offset = 0;
                    byte verLen = ReadByte(recvBuf, ref offset);
                    ReadByte(recvBuf, ref offset);
                    ReadUInt16(recvBuf, ref offset);
                    ReadUInt16(recvBuf, ref offset);
                    ReadUInt16(recvBuf, ref offset);
                    ReadByte(recvBuf, ref offset);
                    ReadByte(recvBuf, ref offset);
                    ReadUInt16(recvBuf, ref offset);
                    ReadUInt32(recvBuf, ref offset);
                    ReadUInt32(recvBuf, ref offset);

                    int ipHeaderLen = (verLen & 0x0F) * 4;
                    if (ipHeaderLen < 20 || ipHeaderLen + 8 > ret)
                        break;

                    offset = ipHeaderLen;
                    byte icmpType = ReadByte(recvBuf, ref offset);
                    byte icmpCode = ReadByte(recvBuf, ref offset);
                    ushort icmpChecksum = ReadUInt16(recvBuf, ref offset);
                    ushort icmpId = ReadUInt16(recvBuf, ref offset);
                    ushort icmpSeq = ReadUInt16(recvBuf, ref offset);

                    if (icmpType == 0)
                    {
                        if (icmpId != pid)
                            continue;

                        gotReply = true;
                        reached = true;
                        break;
                    }

                    if (icmpType == 11)
                    {
                        int innerIpOffset = ipHeaderLen + 8;

                        if (ret >= innerIpOffset + 20)
                        {
                            int innerOffset = innerIpOffset;
                            byte innerVerLen = ReadByte(recvBuf, ref innerOffset);
                            ReadByte(recvBuf, ref innerOffset);
                            ReadUInt16(recvBuf, ref innerOffset);
                            ReadUInt16(recvBuf, ref innerOffset);
                            ReadUInt16(recvBuf, ref innerOffset);
                            ReadByte(recvBuf, ref innerOffset);
                            ReadByte(recvBuf, ref innerOffset);
                            ReadUInt16(recvBuf, ref innerOffset);
                            ReadUInt32(recvBuf, ref innerOffset);
                            ReadUInt32(recvBuf, ref innerOffset);

                            int innerIpLen = (innerVerLen & 0x0F) * 4;
                            if (innerIpLen >= 20 && innerIpOffset + innerIpLen + 8 <= ret)
                            {
                                innerOffset = innerIpOffset + innerIpLen;

                                byte innerType = ReadByte(recvBuf, ref innerOffset);
                                byte innerCode = ReadByte(recvBuf, ref innerOffset);
                                ushort innerChecksum = ReadUInt16(recvBuf, ref innerOffset);
                                ushort innerId = ReadUInt16(recvBuf, ref innerOffset);
                                ushort innerSeq = ReadUInt16(recvBuf, ref innerOffset);

                                if (innerId != pid)
                                    continue;
                            }
                        }

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
                Console.Write($"{hopIp}");
            }

            Console.WriteLine();
            Console.Out.Flush();

            if (reached)
                Console.WriteLine("\nServer was found!");
        }

        Console.WriteLine();
    }

    static byte[] BuildIcmpPacket(ushort id, ushort seq)
    {
        byte[] pkt = new byte[8 + 32];

        pkt[0] = 8;
        pkt[1] = 0;

        pkt[2] = 0;
        pkt[3] = 0;

        pkt[4] = (byte)(id >> 8);
        pkt[5] = (byte)(id & 0xFF);

        pkt[6] = (byte)(seq >> 8);
        pkt[7] = (byte)(seq & 0xFF);

        byte[] payload = Encoding.ASCII.GetBytes("traceroute");
        Array.Copy(payload, 0, pkt, 8, payload.Length);

        ushort cs = CalcChecksum(pkt, 0, pkt.Length);
        pkt[2] = (byte)(cs >> 8);
        pkt[3] = (byte)(cs & 0xFF);

        return pkt;
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
            sum += (uint)(buffer[i] << 8);

        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }

    static void FlushRecvBuffer(Socket sock, byte[] buffer)
    {
        bool oldBlocking = sock.Blocking;
        sock.Blocking = false;

        try
        {
            while (sock.Available > 0)
            {
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
}
