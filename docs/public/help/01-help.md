# Help and Learning - DevThrottle

Welcome to DevThrottle. This is the public help landing page: it explains what
DevThrottle is, what it can do, and where to go for every other piece of
documentation. DevThrottle is fully open source - there is nothing hidden here.

## What is DevThrottle?

DevThrottle is mission control for your coding agents. It runs and supervises
many Claude Code sessions at once, so you can keep a whole fleet of work moving
instead of babysitting one terminal.

The product has three parts that work together:

- **Director** - the desktop application that runs and drives the coding
  sessions on each machine.
- **Gateway** - the service that gathers every machine's Directors into one
  fleet and serves the web dashboard.
- **Cockpit** - the web dashboard, which the Gateway serves to every machine and
  to your phone.

The **Wingman** is the built-in assistant that summarizes sessions and answers
questions about your work and about the product itself.

## Core capabilities

- **Run a fleet of coding sessions.** Start, supervise, and switch between many
  Claude Code sessions across one or more machines from a single place.
- **Embedded terminal per session.** Every session has a real terminal so you
  can watch and steer the agent directly.
- **Source control built in.** Track git changes, clone and manage repositories,
  and review what each session has changed.
- **Voice and Wingman.** Drive sessions hands-free, get spoken briefings, and ask
  the Wingman questions about your sessions.
- **Mobile and in-car use.** Reach the same fleet from your phone, including a
  drive-safe voice mode.
- **Command-line tools.** A set of `cc-*` command-line tools handles document
  conversion, email, browser automation, media processing, and more, all
  designed to work with Claude Code.
- **Control API.** A loopback REST interface lets you drive a Director
  programmatically.

## Where to go next

This public documentation is organized into a few sections. Start wherever fits
your need:

- **[Getting Started: Introduction](../getting-started/01-introduction.md)** -
  the high-level overview of what DevThrottle is and who it is for.
- **[Getting Started: Installation](../getting-started/02-installation.md)** -
  get DevThrottle running on your machine.
- **[Getting Started: Quick Start](../getting-started/03-quick-start.md)** -
  walk through your first session.
- **[Getting Started: Setup Wizard Walkthrough](../getting-started/04-setup-wizard-walkthrough.md)** -
  step-by-step screenshots of the setup wizard.
- **[Features Overview](../features/01-overview.md)** - the user-facing features,
  with screenshots of the running app.
- **[Tools Overview](../tools/01-overview.md)** - every `cc-*` command-line tool
  and how to use it.
- **[Control API](../api/01-control-api.md)** - the REST interface for driving a
  Director programmatically.

## Open source

The full source, documentation, and issue tracker live on GitHub at
[github.com/thefrederiksen/devthrottle](https://github.com/thefrederiksen/devthrottle).
You can browse the code, report issues, and contribute there.
