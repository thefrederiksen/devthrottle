#!/bin/bash
# Starts the Factory Inbox TEST Director with its own computer name, fi-inbox-test.
# Why: the Gateway picks a factory's Director by computer name, and devlinux already runs the owner's
# devlinux Director, which wins the tie. A container gives the test Director its own host name while it
# keeps the host's files (chroot into /), the host network, and the host's process list; it runs as soren.
# Stop it ONLY with its own stop request (root/config/lifecycle-signals); the container ends with it.
set -euo pipefail
BIN=/home/soren/ReposFred/devthrottle-fi-qa/local_builds/linux/cc-director-linux5
docker run -d --rm --name fi-inbox-test-director --network host --hostname fi-inbox-test --pid host --ipc host \
  -v /:/host alpine chroot /host /usr/bin/setpriv --reuid 1000 --regid 1000 --init-groups \
  /usr/bin/env -i DISPLAY=:0 HOME=/home/soren/ReposFred/fi-test-director/home \
  CC_DIRECTOR_ROOT=/home/soren/ReposFred/fi-test-director/root \
  PATH=/home/soren/.local/bin:/home/soren/.local/share/node/bin:/usr/local/bin:/usr/bin:/bin \
  XDG_RUNTIME_DIR=/run/user/1000 LANG=en_US.UTF-8 \
  /bin/bash -c "cd /home/soren/ReposFred/fi-test-director && exec $BIN >> director-stdout.log 2>&1 < /dev/null"
