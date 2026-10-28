"""Local integration fixture; needs Paramiko. Never connects to an external host."""
import base64
import hashlib
import json
import os
import socket
import sys
import threading
import time
import paramiko

root = os.path.abspath(sys.argv[1])
os.makedirs(root, exist_ok=True)
paramiko.util.log_to_file(os.path.join(root, 'ssh.log'))
key = paramiko.ECDSAKey.generate()
host_key = key
key_path = os.path.join(root, 'client.key')
key.write_private_key_file(key_path)


class Auth(paramiko.ServerInterface):
    def get_allowed_auths(self, username):
        return 'password,publickey'

    def check_auth_password(self, username, password):
        return paramiko.AUTH_SUCCESSFUL if username == 'test' and password == 'fixture-password' else paramiko.AUTH_FAILED

    def check_auth_publickey(self, username, public_key):
        return paramiko.AUTH_SUCCESSFUL if username == 'test' and public_key == key else paramiko.AUTH_FAILED

    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == 'session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_exec_request(self, channel, command):
        def execute():
            try:
                global host_key
                text = command.decode('utf-8')
                if text == 'rotate-host-key':
                    host_key = paramiko.ECDSAKey.generate()
                if text == 'slow':
                    time.sleep(10)
                if text == 'fail':
                    channel.send_stderr(b'command failed')
                    channel.send_exit_status(7)
                else:
                    channel.send(('executed:' + text).encode('utf-8'))
                    channel.send_exit_status(0)
                channel.close()
            except (OSError, EOFError):
                pass
        threading.Thread(target=execute, daemon=True).start()
        return True


class Files(paramiko.SFTPServerInterface):
    def path(self, name):
        value = os.path.abspath(os.path.join(root, name.lstrip('/')))
        if os.path.commonpath([root, value]) != root:
            raise PermissionError('outside fixture')
        return value

    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.stat(self.path(path)))
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    lstat = stat

    def open(self, path, flags, attr):
        try:
            fd = os.open(self.path(path), flags | getattr(os, 'O_BINARY', 0), 0o600)
            mode = 'r+b' if flags & os.O_RDWR else 'wb' if flags & os.O_WRONLY else 'rb'
            file = os.fdopen(fd, mode)
            handle = paramiko.SFTPHandle(flags)
            if flags & (os.O_WRONLY | os.O_RDWR):
                handle.writefile = file
            if not flags & os.O_WRONLY:
                handle.readfile = file
            handle.stat = lambda: paramiko.SFTPAttributes.from_stat(os.fstat(file.fileno()))
            return handle
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)


listener = socket.socket()
listener.bind(('127.0.0.1', 0))
listener.listen(10)
print(json.dumps({'port': listener.getsockname()[1], 'fingerprint': 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('='), 'key': key_path}), flush=True)


def serve(sock):
    transport = paramiko.Transport(sock)
    try:
        transport.add_server_key(host_key)
        transport.set_subsystem_handler('sftp', paramiko.SFTPServer, Files)
        transport.start_server(server=Auth())
        channels = []
        while transport.is_active():
            channel = transport.accept(0.2)
            if channel is not None:
                channels.append(channel)
    except (OSError, EOFError, paramiko.SSHException):
        pass
    finally:
        transport.close()


while True:
    client, _ = listener.accept()
    threading.Thread(target=serve, args=(client,), daemon=True).start()
