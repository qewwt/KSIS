import socket
import threading
import struct
import datetime
import sys

MSG_CHAT  = 1
MSG_NAME  = 2
MSG_JOIN  = 3
MSG_LEAVE = 4

def pack_message(msg_type: int, text: str) -> bytes:
    data = text.encode('utf-8')
    return struct.pack('>BI', msg_type, len(data)) + data

def recv_exact(sock, n):
    buf = b''
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf

def recv_message(sock):
    header = recv_exact(sock, 5)
    if not header:
        return None
    msg_type, length = struct.unpack('>BI', header)
    body = recv_exact(sock, length)
    if body is None:
        return None
    return msg_type, body.decode('utf-8')

def timestamp():
    return datetime.datetime.now().strftime('%H:%M:%S')

TYPE_LABELS = {
    MSG_CHAT:  "сообщение",
    MSG_JOIN:  "подключение",
    MSG_LEAVE: "отключение",
}

def receive_loop(sock):
    """Поток: читает входящие сообщения и печатает в консоль."""
    while True:
        result = recv_message(sock)
        if not result:
            print(f"\n[{timestamp()}] Соединение с сервером разорвано.")
            sys.exit(0)
        msg_type, text = result
        label = TYPE_LABELS.get(msg_type, "?")
        # Перепечатываем поверх строки ввода
        sys.stdout.write(f"\r[{timestamp()}][{label}] {text}\n> ")
        sys.stdout.flush()

if __name__ == '__main__':
    name        = input("Ваше имя: ").strip()
    server_ip   = input("IP сервера: ").strip()
    server_port = int(input("Порт сервера: ").strip())

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.connect((server_ip, server_port))
    except ConnectionRefusedError:
        print("Не удалось подключиться. Проверьте, запущен ли сервер!")
        sys.exit(1)

    # Первым делом — отправляем имя
    sock.sendall(pack_message(MSG_NAME, name))
    print(f"[{timestamp()}] Подключён к {server_ip}:{server_port} как '{name}'")

    # Запускаем поток чтения
    t = threading.Thread(target=receive_loop, args=(sock,), daemon=True)
    t.start()

    # Главный поток — ввод сообщений
    try:
        while True:
            sys.stdout.write("> ")
            sys.stdout.flush()
            text = input()
            if text.strip():
                sock.sendall(pack_message(MSG_CHAT, text))
    except (KeyboardInterrupt, EOFError):
                print(f"\n[{timestamp()}] Отключение...")
                sock.shutdown(socket.SHUT_RDWR)
                sock.close()

