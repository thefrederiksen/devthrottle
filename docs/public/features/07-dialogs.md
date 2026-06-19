# Key Dialogs

A few dialogs do most of the heavy lifting for setup and configuration.

## New Session dialog

The New Session dialog creates a single session - pick the agent, the identity
(Developer, QA, Product, and so on), the project, the model, and the effort level
- or launch a preset multi-session group as one unit.

_Screenshot pending - the dialog is captured when `/document-features` runs on an
interactive desktop. Modal dialogs only render when the app window can be brought
to the foreground, which the automated background pass cannot do._

## Settings dialog

The Settings dialog is the multi-tab application configuration: permission mode,
model override, effort level, and more, with read-only previews of the launch
command each choice produces.

_Screenshot pending - captured when `/document-features` runs on an interactive
desktop (modal dialogs require the window to be foreground)._

## Workflow editor (partial)

The Workflow Editor is a three-column recorder and editor for desktop automation
workflows (a step list, a step editor, and a canvas/preview). The framework is in
place; not every step type is complete yet.

_Screenshot pending - open the Workflow Editor to capture._
