"""The askpass helper `cc-secrets run --via askpass` points SUDO_ASKPASS / SSH_ASKPASS / GIT_ASKPASS at.

It is run BY the command (sudo, ssh, git), never by the agent. It connects to the one-shot loopback
listener the runner opened for this single command, proves it holds that run's random token, and
prints the secret to its own standard output, which the asking program reads directly. The secret is
never on disk and never in an environment variable - only the port and the one-run token are.

Deliberately standalone (standard library only, no package imports) so it runs by file path.
"""

import os
import socket
import sys


def main() -> int:
    port = int(os.environ["CC_SECRETS_ASKPASS_PORT"])
    token = os.environ["CC_SECRETS_ASKPASS_TOKEN"]
    with socket.create_connection(("127.0.0.1", port), timeout=10) as conn:
        conn.sendall(token.encode("ascii") + b"\n")
        chunks = []
        while True:
            data = conn.recv(4096)
            if not data:
                break
            chunks.append(data)
    reply = b"".join(chunks)
    if not reply.startswith(b"OK\n"):
        sys.stderr.write("cc-secrets askpass: the listener refused this request\n")
        return 1
    sys.stdout.write(reply[3:].decode("utf-8") + "\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
