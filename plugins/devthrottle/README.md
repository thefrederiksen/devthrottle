# DevThrottle

Skills for people who run [DevThrottle](https://devthrottle.com) - mission control for
command-line coding agents. Run a fleet of agents at once, each on its own repository,
from one app, and steer them from your phone.

If you do not run DevThrottle, install
[`agent-discipline`](../agent-discipline) from the same marketplace instead - it is
product-free and needs nothing.

```
/plugin marketplace add thefrederiksen/devthrottle
/plugin install devthrottle@devthrottle
```

## What is in it

**`devthrottle-install`** - the documented steps to install DevThrottle and connect a
gateway. The skill hands the user the steps and stops there. It does not download an
installer and it does not pipe a remote script into a shell, deliberately.

**`devthrottle-sessions`** - how one session reaches the others: list what is running
across your machines, name this session, send one of the rare queued messages and read
the inbox, ask for a reply without waiting for it, open a new session, and close one down.

MIT licensed. Source: <https://github.com/thefrederiksen/devthrottle>
