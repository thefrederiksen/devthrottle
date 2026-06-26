# How a Terminal Works -- Deep Dive

Educational reference -- no code changes needed.

Related reference: [Supported agents and terminal screen behavior](SupportedAgentsTerminalModes.md).

---

## The Big Picture

A "terminal" is a character grid. Think of it like a spreadsheet where every
cell holds exactly one character with some styling (color, bold, etc.).

```
    Col 0   Col 1   Col 2   Col 3   Col 4   ... Col 119
   +-------+-------+-------+-------+-------+---+-------+
Row 0  |   H   |   e   |   l   |   l   |   o   |   |       |
   +-------+-------+-------+-------+-------+---+-------+
Row 1  |   $   |   _   |       |       |       |   |       |  <-- cursor at (1,1)
   +-------+-------+-------+-------+-------+---+-------+
Row 2  |       |       |       |       |       |   |       |
   +-------+-------+-------+-------+-------+---+-------+
  ...
   +-------+-------+-------+-------+-------+---+-------+
Row 39 |       |       |       |       |       |   |       |
   +-------+-------+-------+-------+-------+---+-------+

         120 columns x 40 rows = 4,800 cells
```

Each cell is a struct:
```
  TerminalCell {
      Character:  'H'
      Foreground: #00FF00  (green)
      Background: #000000  (black)
      Bold:       false
      Italic:     false
      Underline:  false
  }
```

## How Characters Arrive -- Yes, Always Serial

Data arrives as a **serial byte stream**. One byte at a time, in order.
There is only ONE input pipe from the program to the terminal.

```
  Program (claude.exe)            Terminal (our code)
  ====================            ===================

  stdout/stderr  ------pipe------>  byte[] buffer
                   serial bytes
                   one after another

  "Hello\n"  =  0x48 0x65 0x6C 0x6C 0x6F 0x0A
                  H    e    l    l    o    LF
```

Even when you see the screen update "all at once", it's really thousands
of bytes arriving very fast in sequence. Our code reads them in chunks
(every 50ms we grab whatever's accumulated in the buffer).

## The Cursor -- The Invisible Pen

The terminal has an invisible cursor position: (col, row). Every printable
character gets written AT the cursor, then the cursor moves right by one.

```
  State: cursor at (0, 0)

  Receive: H e l l o

  Step 1: Write 'H' at (0,0), cursor -> (1,0)
  Step 2: Write 'e' at (1,0), cursor -> (2,0)
  Step 3: Write 'l' at (2,0), cursor -> (3,0)
  Step 4: Write 'l' at (3,0), cursor -> (4,0)
  Step 5: Write 'o' at (4,0), cursor -> (5,0)
```

## Control Characters -- Special Bytes

Some bytes are not printable -- they're instructions:

```
  Byte    Name     What it does
  ----    ----     --------------------------------
  0x0A    LF       Line Feed: cursor moves down one row
  0x0D    CR       Carriage Return: cursor moves to column 0
  0x08    BS       Backspace: cursor moves left one
  0x09    TAB      Tab: cursor jumps to next 8-column stop
  0x07    BEL      Bell: beep (ignored visually)
  0x1B    ESC      Escape: starts an ANSI escape sequence
```

A newline in terminal-land is CR+LF (0x0D 0x0A):
```
  Before:  cursor at (5, 0)
  Receive: CR LF
  After CR: cursor at (0, 0)   -- moved to start of line
  After LF: cursor at (0, 1)   -- moved down one row
```

## ANSI Escape Sequences -- The Real Magic

This is how programs update specific positions, change colors, clear
the screen, etc. It all goes through the same serial byte stream.

An escape sequence starts with ESC (0x1B) followed by more bytes:

```
  ESC [ <params> <final_byte>

  This is called a "CSI sequence" (Control Sequence Introducer)
```

### Moving the cursor to any position:

```
  ESC [ row ; col H        -- "CUP" = Cursor Position

  Example: ESC[5;10H  =  move cursor to row 5, column 10

  Bytes: 1B 5B 35 3B 31 30 48
         ESC [  5  ;  1  0  H
```

This is how Claude Code draws its UI. It doesn't print line by line --
it jumps the cursor around and overwrites cells:

```
  1. ESC[1;1H    -- go to top-left
  2. Print "Claude Code v2.1.71"
  3. ESC[3;1H    -- jump to row 3
  4. Print "Welcome back User!"
  5. ESC[5;1H    -- jump to row 5
  6. ESC[7m      -- turn on reverse video
  7. Print " Recent activity "
  8. ESC[0m      -- reset colors
```

### Changing colors (SGR = Select Graphic Rendition):

```
  ESC [ <code> m

  Common codes:
  0     Reset all attributes
  1     Bold
  3     Italic
  4     Underline
  7     Reverse video (swap fg/bg)

  30-37   Set foreground color (8 basic colors)
  40-47   Set background color
  90-97   Set bright foreground

  38;5;N     Set foreground to 256-color palette
  38;2;R;G;B Set foreground to true color (24-bit)
```

Example -- print "Error" in red bold:
```
  ESC[1;31m  Error  ESC[0m

  Bytes: 1B 5B 31 3B 33 31 6D  45 72 72 6F 72  1B 5B 30 6D
         ESC[ 1  ;  3  1  m    E  r  r  o  r   ESC[ 0  m
         |-- bold+red ---|    |-- text -----|   |--reset--|
```

### Erasing:

```
  ESC[2J     -- erase entire screen (all cells become empty)
  ESC[K      -- erase from cursor to end of line
  ESC[1K     -- erase from start of line to cursor
```

### Scrolling:

When the cursor is at the bottom row and a LF arrives, the entire
grid scrolls up: row 0 is saved to "scrollback", all rows shift up
by one, and the bottom row becomes empty.

```
  BEFORE scroll (cursor at bottom):       AFTER scroll:

  Row 0: "first line"                     Row 0: "second line"
  Row 1: "second line"                    Row 1: "third line"
  Row 2: "third line"                     Row 2: "fourth line"
  Row 3: "fourth line"    <-- cursor      Row 3: ""                <-- cursor

                                          Scrollback: ["first line"]
```

## How Claude Code's User Interface is Built

Claude Code does not just print text -- current versions draw a full screen terminal user interface using cursor positioning and the alternate screen buffer. See [Supported agents and terminal screen behavior](SupportedAgentsTerminalModes.md) for the full agent matrix. Here is what the byte stream looks like when it draws its welcome screen:

```
  SERIAL BYTE STREAM (simplified):
  ==================================

  ESC[?1049h           -- Switch to alternate screen buffer
  ESC[2J               -- Clear entire screen
  ESC[?25l             -- Hide cursor

  ESC[1;1H             -- Position: row 1, col 1
  ESC[1;36m            -- Color: bold cyan
  Claude Code          -- Print text (6 chars written at cursor)

  ESC[1;20H            -- Jump to row 1, col 20
  ESC[0mv2.1.71        -- Reset color, print version

  ESC[3;1H             -- Jump to row 3
  ESC[1mWelcome back   -- Bold, print text
  ESC[0m               -- Reset

  ESC[5;1H             -- Jump to row 5
  ESC[7m               -- Reverse video ON
  ESC[K                -- Clear line (fills with reverse bg)
   Recent activity     -- Print (appears as white-on-blue bar)
  ESC[0m               -- Reset

  ... more positioning + printing ...

  ESC[39;1H            -- Jump to bottom row
  ESC[7m > ESC[0m      -- Draw the prompt ">"
  ESC[?25h             -- Show cursor
```

Key insight: **Every character position is explicitly set.** The program
doesn't rely on natural text flow -- it moves the cursor to exact (col,row)
coordinates and writes characters there. This is why a terminal is a GRID,
not a text document.

## The Size -- Who Decides?

```
  +------------------------------------------+
  | Terminal Window (WPF control)             |
  |                                           |
  |  Width:  960 pixels                       |
  |  Height: 520 pixels                       |
  |  Font:   Cascadia Mono, 13px              |
  |  Cell:   8px wide x 16px tall             |
  |                                           |
  |  cols = 960 / 8  = 120                    |
  |  rows = 520 / 16 = 32                     |
  |                                           |
  |  The terminal tells the program:          |
  |  "You have 120 columns and 32 rows"       |
  |                                           |
  |  Program formats output for that size.    |
  +------------------------------------------+
        |
        | (ConPTY tells the child process)
        v
  claude.exe knows: "I have 120x32 to work with"
  -> draws status bar at row 32
  -> wraps text at column 120
  -> centers content based on width
```

When the window resizes, the terminal recalculates cols/rows and
tells the program via ConPTY. The program then redraws everything
for the new size.

## The Full Pipeline in CC Director

```
  claude.exe (child process)
       |
       | stdout bytes (serial ANSI stream)
       v
  ConPTY (Windows pseudo-terminal)
       |
       | raw bytes
       v
  CircularTerminalBuffer (ring buffer, stores last N bytes)
       |
       | GetWrittenSince(position) -> byte[]
       v
  TerminalControl (polls every 50ms)
       |
       | feeds bytes to:
       v
  AnsiParser (state machine)
       |
       | Processes each byte:
       |   - Printable? -> write to cells[col, row], advance cursor
       |   - ESC?       -> enter escape mode, parse sequence
       |   - CSI H?     -> move cursor to position
       |   - CSI m?     -> change current fg/bg/bold
       |   - LF?        -> move cursor down (scroll if at bottom)
       |   - CR?        -> move cursor to column 0
       v
  TerminalCell[cols, rows]  (the grid -- THIS is the "screen")
       |
       | OnRender() reads the grid
       v
  ITerminalRenderer (ORG/PRO/LITE)
       |
       | DrawingContext: draws each cell as text at pixel position
       v
  WPF renders to screen
```

## The Parser State Machine

The AnsiParser is a simple state machine with 4 states:

```
                   ESC (0x1B)
  +-------+  ----------------->  +--------+
  | Ground |                     | Escape |
  |       |  <----- other ---   |        |
  +-------+                     +--------+
     ^  ^                          |
     |  |                    '[' (0x5B)
     |  |                          |
     |  |                          v
     |  |    final byte         +-----+
     |  +------- (letter) ---  | CSI  |
     |                          |     |  <-- digits, semicolons
     |                          +-----+      (collecting params)
     |           ']'
     |  +--------+
     +--| OSC    | <-- ESC ]  (Operating System Command)
        +--------+     terminated by BEL or ST
```

Ground state: normal character processing
Escape state: just saw ESC, waiting to see what kind of sequence
CSI state:    collecting parameters (digits + semicolons) for a command
OSC state:    collecting string data (window title, hyperlinks, etc.)

## Why CARD Mode Needed WebView2

The cell grid is fundamental to how terminals work. ORG/PRO/LITE all
read the same grid and paint characters at pixel positions:

```
  cells[5, 2].Character = 'H'   -->  draw 'H' at pixel (40, 32)
  cells[5, 2].Foreground = Green -->  in green color
```

You CAN'T get web-page aesthetics from this because:
- Every character occupies exactly one cell (fixed width)
- No word wrapping, no variable-width text
- No rounded corners, shadows, or padding between content groups
- No HTML/CSS layout engine

CARD mode solves this by reading the SAME cell grid but converting it
to HTML, then rendering in WebView2 which has a full CSS layout engine.

```
  Same cell grid --> AnsiToHtmlConverter --> HTML + CSS --> WebView2
                     (reads cells,           (cards,       (real browser
                      groups into blocks,     shadows,      engine with
                      generates <span>s)      rounded       full CSS)
                                              corners)
```
