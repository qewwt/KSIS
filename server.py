import socket
import threading
import struct
import datetime

# Формат пакета: 1 байт тип + 4 байта длина + N байт данные
# Типы: 1=сообщение, 2=имя, 3=подключение, 4=отключение

MSG_CHAT    = 1
MSG_NAME    = 2
MSG_JOIN    = 3
MSG_LEAVE   = 4

def pack_message(msg_type: int, text: str) -> bytes:
    data = text.encode('utf-8')
    return struct.pack('>BI', msg_type, len(data)) + data

def recv_exact(sock, n):
    """Читает ровно n байт из сокета (TCP может дробить пакеты)."""
    buf = b''
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf

def recv_message(sock):
    """Читает один пакет из сокета. Возвращает (тип, текст) или None."""
    header = recv_exact(sock, 5)  # 1 + 4
    if not header:
        return None
    msg_type, length = struct.unpack('>BI', header)
    body = recv_exact(sock, length)
    if body is None:
        return None
    return msg_type, body.decode('utf-8')

def timestamp():
    return datetime.datetime.now().strftime('%H:%M:%S')

class Server:
    def __init__(self, ip, port):
        self.ip = ip
        self.port = port
        # {socket: {"name": str, "addr": str}}
        self.clients = {}
        self.lock = threading.Lock()

    def broadcast(self, msg_type, text, exclude=None):
        packet = pack_message(msg_type, text)
        with self.lock:
            for sock in list(self.clients):
                if sock is exclude:
                    continue
                try:
                    sock.sendall(packet)
                except:
                    pass

    def handle_client(self, conn, addr):
        ip = addr[0]
        # Первым пакетом клиент присылает своё имя
        result = recv_message(conn)
        if not result or result[0] != MSG_NAME:
            conn.close()
            return
        name = result[1]

        with self.lock:
            self.clients[conn] = {"name": name, "addr": ip}

        join_text = f"{name} подключился"
        print(f"[{timestamp()}] {join_text}")
        # Уведомить всех кроме нового клиента
        self.broadcast(MSG_JOIN, join_text, exclude=conn)
        # Отправить новому клиенту список уже подключённых
        with self.lock:
            for s, info in self.clients.items():
                if s is not conn:
                    conn.sendall(pack_message(MSG_JOIN,
                        f"{info['name']} уже в чате"))

        try:
            while True:
                result = recv_message(conn)
                if not result:
                    break
                msg_type, text = result
                if msg_type == MSG_CHAT:
                    full = f"{name}: {text}"
                    print(f"[{timestamp()}] {full}")
                    self.broadcast(MSG_CHAT, full, exclude=conn)
        except:
            pass
        finally:
            with self.lock:
                self.clients.pop(conn, None)
            leave_text = f"{name} отключился"
            print(f"[{timestamp()}] {leave_text}")
            self.broadcast(MSG_LEAVE, leave_text)
            conn.close()

    def start(self):
        server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            server_sock.bind((self.ip, self.port))
        except OSError as e:
            print(f"Ошибка: не удалось занять {self.ip}:{self.port} — {e}")
            return
        server_sock.listen(10)
        print(f"[{timestamp()}] Сервер запущен на {self.ip}:{self.port}")
        while True:
            try:
                conn, addr = server_sock.accept()
                t = threading.Thread(target=self.handle_client,
                                     args=(conn, addr), daemon=True)
                t.start()
            except KeyboardInterrupt:
                print("\nСервер остановлен.")
                break
        server_sock.close()

if __name__ == '__main__':
    ip   = input("IP сервера (например 127.0.0.1): ").strip()
    port = int(input("Порт (например 9000): ").strip())
    Server(ip, port).start()
