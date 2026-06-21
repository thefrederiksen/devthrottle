# CC Director v2 -- WPF Design Specification

This document is a complete implementation guide for rebuilding the CC Director UI in WPF.
It contains every color, font, spacing value, and layout measurement extracted from the
approved Pencil design file. An implementing agent should be able to produce a pixel-accurate
WPF application from this document alone.

Reference design: the approved Pencil design file (see UI_Redesign_Handover.md).

---

## 1. Design Tokens (Resource Dictionary)

### 1.1 Color Palette

```xml
<!-- Backgrounds -->
<Color x:Key="BgPage">#FF1B1E24</Color>
<Color x:Key="BgSurface">#FF232730</Color>
<Color x:Key="BgElevated">#FF2C313B</Color>
<Color x:Key="BgInput">#FF2C313B</Color>

<!-- Borders -->
<Color x:Key="Border">#FF363C48</Color>
<Color x:Key="BorderSubtle">#FF2C313B</Color>

<!-- Text -->
<Color x:Key="TextPrimary">#FFE8EAED</Color>
<Color x:Key="TextSecondary">#FF9AA0AC</Color>
<Color x:Key="TextTertiary">#FF6B7280</Color>
<Color x:Key="TextMuted">#FF4B5263</Color>

<!-- Accents -->
<Color x:Key="AccentBlue">#FF5B9CF6</Color>
<Color x:Key="AccentGreen">#FF6BCB8B</Color>
<Color x:Key="AccentWarning">#FFE5A54B</Color>
<Color x:Key="AccentError">#FFE55B5B</Color>
<Color x:Key="AccentNeutral">#FF4B5263</Color>

<!-- Special -->
<Color x:Key="NotificationBg">#FF2A2520</Color>
<Color x:Key="NotificationBorder">#FF3D3528</Color>
<Color x:Key="BadgeBlueBg">#FF1E2A3D</Color>
```

### 1.2 SolidColorBrush equivalents

Create a `SolidColorBrush` for each color above, e.g.:
```xml
<SolidColorBrush x:Key="BgPageBrush" Color="{StaticResource BgPage}" />
```

### 1.3 Typography

Two font families are used throughout:

| Token | Family | Use |
|-------|--------|-----|
| `FontHeading` | **Inter** | Titles, session names, logo text, metric values |
| `FontMono` | **JetBrains Mono** | Navigation, labels, buttons, data, terminal content, badges |

Type scale:

| Size | Weight | Use |
|------|--------|-----|
| 18px | SemiBold (600) | Session name in session bar |
| 15px | SemiBold (600) | Claude Code version title |
| 13px | SemiBold (600) | App title "CC DIRECTOR" (letterSpacing: 3px), session card names |
| 13px | Normal (400) | Terminal prompt text |
| 12px | SemiBold (600) | Tab labels (active), button text ("Send", "New Session") |
| 12px | Medium (500) | Button text ("Queue"), tab labels (active inspector) |
| 12px | Normal (400) | Terminal body text, model info |
| 11px | SemiBold (600) | Turn badge text, percentage values |
| 11px | Medium (500) | App bar labels, session bar button text, status bar profile |
| 11px | Normal (400) | App bar stat labels, stat time values, notification text, directory path |
| 10px | Medium (500) | Status badge text, uncommitted count |
| 10px | Normal (400) | Claude ID values, Director ID values, status bar labels, PID text |
| 9px | Bold (700) | "READ-ONLY" badge, Source Control change count badge |
| 9px | SemiBold (600) | Re-link button text |
| 9px | Medium (500) | Uncommitted badge text, action button text ("Open", "Relink") |
| 9px | Normal (400) | Session ID hash text |

---

## 2. Layout Structure (1440 x 900)

The app is a single window, `1440px` wide by `900px` tall, with `clip: true`.

### 2.1 Top-Level Vertical Stack

```
+--------------------------------------------------+
| TitleBar           (h: 36)                        |
+--------------------------------------------------+
| BodyRow            (h: fill)                      |
|  +----------+--+------------------+--+----------+ |
|  | Sidebar  |  | CenterPanel      |  | Inspector| |
|  | w:240    |  | w:fill           |  | w:300    | |
|  +----------+--+------------------+--+----------+ |
+--------------------------------------------------+
| StatusBar          (h: 28)                        |
+--------------------------------------------------+
```

### 2.2 WPF Layout Mapping

| Design concept | WPF control |
|----------------|-------------|
| Vertical stack (layout: vertical) | `DockPanel` or `Grid` with row definitions |
| Horizontal stack (layout: horizontal) | `StackPanel Orientation="Horizontal"` or `Grid` with column definitions |
| fill_container | `"*"` in Grid or `HorizontalAlignment="Stretch"` |
| fit_content | `Auto` sizing |
| gap | Use `Margin` on children or `UniformGrid` spacing |
| padding | `Padding` property on container |
| clip: true | `ClipToBounds="True"` |

---

## 3. Component Specifications

### 3.1 Title Bar

- **Height:** 36px
- **Background:** `BgPage` (#1B1E24)
- **Border:** 1px bottom, `Border` (#363C48)
- **Padding:** 0 vertical, 12px horizontal
- **Layout:** Horizontal, center-aligned vertically, gap 12px

Contents (left to right):
1. **Hamburger icon** -- Lucide `menu`, 16x16, fill `TextTertiary`
2. **App title** -- "CC DIRECTOR", Inter 13px SemiBold, fill `TextPrimary`, letterSpacing 3px
3. **Read-only badge** -- Frame with `BgElevated` bg, cornerRadius 3, padding [2, 8]
   - Text: "READ-ONLY", JetBrains Mono 9px Bold, fill `AccentBlue`
4. **Spacer** -- fills remaining width
5. **Window controls** -- horizontal, gap 16px
   - Minimize (Lucide `minus` 14x14), Maximize (Lucide `square` 14x14), Close (Lucide `x` 14x14)
   - All fill `TextTertiary`

### 3.2 Sidebar (Left Panel)

- **Width:** 240px fixed
- **Height:** fill container
- **Background:** `BgPage` (#1B1E24)
- **Border:** 1px right, `Border` (#363C48)
- **Padding:** 0 vertical, 12px horizontal
- **Layout:** Vertical, gap 4px

#### 3.2.1 Sidebar Header
- **Height:** 40px
- **Padding:** 8px all
- **Layout:** Horizontal, center-aligned, gap 8px
- "SESSIONS" -- JetBrains Mono 11px Medium, `TextTertiary`, letterSpacing 2px
- Spacer
- "5" -- JetBrains Mono 11px Normal, `TextMuted`

#### 3.2.2 New Session Button
- **Width:** fill container
- **Height:** 36px
- **Background:** `AccentBlue` (#5B9CF6)
- **CornerRadius:** 4px
- **Padding:** 0 vertical, 12px horizontal
- **Layout:** Horizontal, center content, gap 8px
- Plus icon: Lucide `plus` 14x14, white (#FFFFFF)
- "New Session" -- JetBrains Mono 12px SemiBold, white (#FFFFFF)

#### 3.2.3 Session Cards (in scrollable list, gap 6px between cards)

**Active Session Card:**
- **Width:** fill
- **Background:** `BgSurface` (#232730)
- **CornerRadius:** 4px
- **Border:** 1px all sides, `Border` (#363C48)
- **Padding:** 12px vertical, 14px horizontal
- **Layout:** Vertical, gap 8px

Contents:
1. **Session name** -- Inter 13px SemiBold, `TextPrimary` (#E8EAED)
2. **Meta row** (horizontal, gap 8px, center aligned):
   - Status dot: 6x6 rectangle, cornerRadius 3, fill `AccentGreen` (#6BCB8B)
   - "Your Turn" -- JetBrains Mono 10px Medium, `AccentGreen`
   - "PID 17560" -- JetBrains Mono 10px Normal, `TextMuted`
3. **Actions row** (horizontal, gap 6px):
   - ID tag: `BgElevated` bg, padding [2,6], gap 4
     - Check icon: Lucide `circle-check` 10x10, `AccentGreen`
     - "6da2ddb2..." -- JetBrains Mono 9px Normal, `TextTertiary`
   - Uncommitted badge: `BadgeBlueBg` (#1E2A3D) bg, padding [2,6]
     - "44 uncommitted" -- JetBrains Mono 9px Medium, `AccentBlue`
   - Open button: `BgElevated` bg, padding [2,8]
     - "Open" -- JetBrains Mono 9px Medium, `TextSecondary`
   - Relink button: `BgElevated` bg, padding [2,8]
     - "Relink" -- JetBrains Mono 9px Medium, `TextSecondary`

**Inactive Session Card:**
- Same structure as active, but:
- **Background:** `BgPage` (#1B1E24)
- **Border:** 1px, `BorderSubtle` (#2C313B)
- **Session name color:** `TextSecondary` (#9AA0AC) instead of `TextPrimary`

#### 3.2.4 Sidebar Footer (Usage Stats)
- **Background:** `BgSurface` (#232730)
- **Border:** 1px top, `Border` (#363C48)
- **Padding:** 16px vertical, 12px horizontal
- **Layout:** Vertical, gap 10px

Contents:
1. "USAGE" -- JetBrains Mono 9px Bold, `TextMuted`, letterSpacing 2px
2. **5h row** (horizontal, fill width):
   - "5h" -- JetBrains Mono 11px, `TextTertiary`
   - Spacer
   - "71%" -- Inter 13px SemiBold, `AccentBlue`
3. **Progress bar** -- 3px height, `BgElevated` track, `AccentBlue` fill (71% width)
4. **7d row** (same layout):
   - "7d" -- `TextTertiary`
   - "31%" -- Inter 13px SemiBold, `TextSecondary`
5. **Progress bar** -- 3px height, `BgElevated` track, `TextTertiary` fill (31% width)
6. **Spent row**:
   - "Spent" -- JetBrains Mono 10px, `TextMuted`
   - Spacer
   - "$71 / $200" -- JetBrains Mono 10px Medium, `TextSecondary`

### 3.3 Center Panel

- **Width:** fill (stretches between sidebar and inspector)
- **Height:** fill
- **Layout:** Vertical stack, no gap

#### 3.3.1 App Bar (Metrics Bar)
- **Height:** 36px
- **Background:** `BgSurface` (#232730)
- **Border:** 1px bottom, `Border`
- **Padding:** 0 vertical, 20px horizontal
- **Layout:** Horizontal, center-aligned, gap 16px

Contents:
- "AcmeCorp:" -- JetBrains Mono 11px Medium, `TextSecondary`
- 5h stat group (gap 4): "5h:" `TextTertiary`, "71%" `AccentBlue` SemiBold, "(25m)" `TextMuted`
- 7d stat group (gap 4): "7d:" `TextTertiary`, "31%" `TextSecondary` Medium, "(6d, Mar 5)" `TextMuted`
- Spent group (gap 4): "Spent:" `TextTertiary`, "$71" `AccentWarning` SemiBold, "/ Limit: $200" `TextMuted`
- Spacer
- Settings icon: Lucide `settings` 16x16, `TextTertiary`
- Help icon: Feather `help-circle` 16x16, `TextTertiary`

#### 3.3.2 Session Bar (visible when session selected)
- **Background:** `BgSurface` (#232730)
- **Border:** 1px bottom, `Border`
- **Padding:** 12px vertical, 20px horizontal
- **Layout:** Vertical, gap 8px

**Top row** (horizontal, center-aligned, gap 12):
- Session name: "cc-consult" -- Inter 18px SemiBold, `TextPrimary`
- Spacer
- Refresh button: `BgElevated` bg, cornerRadius 4, padding [6, 12], gap 6
  - Lucide `refresh-cw` 12x12 `TextTertiary` + "Refresh" JetBrains Mono 11px Medium `TextSecondary`
- Your Turn badge: `BadgeBlueBg` (#1E2A3D) bg, cornerRadius 3, padding [4, 10], gap 6
  - Green dot 6x6 cornerRadius 3 + "Your Turn" JetBrains Mono 11px SemiBold `AccentGreen`
- Messages badge: `BgElevated` bg, cornerRadius 3, padding [4, 10], gap 6
  - Lucide `message-square` 12x12 `AccentBlue` + "3 msgs" JetBrains Mono 11px Medium `AccentBlue`

**Bottom row** (horizontal, center-aligned, gap 16):
- Claude ID group (gap 6):
  - "Claude ID:" JetBrains Mono 10px `TextMuted`
  - UUID value: JetBrains Mono 10px `AccentBlue`
  - Re-link button: `AccentError` bg, cornerRadius 3, padding [2, 6]
    - "Re-link" JetBrains Mono 9px SemiBold `TextPrimary`
- Separator: 1px wide, 12px tall, `Border`
- Director ID group (gap 6):
  - "Director ID:" JetBrains Mono 10px `TextMuted`
  - UUID value: JetBrains Mono 10px `TextTertiary`

#### 3.3.3 Tab Bar
- **Height:** 36px
- **Background:** `BgSurface` (#232730)
- **Border:** 1px bottom, `Border`
- **Padding:** 0 vertical, 12px horizontal
- **Layout:** Horizontal, center-aligned

**Active tab:**
- **Background:** `BgElevated` (#2C313B)
- **Height:** fill
- **Padding:** 0 vertical, 16px horizontal
- **Bottom border:** 2px, `AccentBlue`
- Text: JetBrains Mono 12px SemiBold, `TextPrimary`

**Inactive tab:**
- **No background**
- **Height:** fill
- **Padding:** 0 vertical, 16px horizontal
- Text: JetBrains Mono 12px Normal, `TextTertiary`
- Optional badge: `AccentWarning` bg, padding [1, 6]
  - Count: JetBrains Mono 9px Bold, `BgPage` color

**Collapse button** (rightmost): Lucide `chevrons-right` 14x14, `TextMuted`

#### 3.3.4 Terminal Area
- **Background:** `BgPage` (#1B1E24)
- **Height:** fill (takes remaining space)
- **Padding:** 20px vertical, 24px horizontal
- **Clip:** true
- **Layout:** Vertical, gap 16px

**IMPORTANT:** This area hosts an embedded ConPTY terminal window. The design shows
sample content for reference only. In WPF, this will be a terminal host control
(e.g., `Microsoft.Terminal.Wpf` or a custom ConPTY integration). The CC logo block
and Claude Code info shown in the screenshot are rendered BY the terminal, not by WPF.

The WPF implementation should:
1. Create a container with the specified background/padding
2. Host the ConPTY terminal inside it
3. The terminal renders its own content -- do not attempt to replicate it in XAML

#### 3.3.5 Notification Bar
- **Height:** 28px
- **Background:** `NotificationBg` (#2A2520)
- **Border:** 1px top and bottom, `NotificationBorder` (#3D3528)
- **Padding:** 0 vertical, 20px horizontal
- **Layout:** Horizontal, center-aligned, gap 8px
- Icon: Lucide `info` 12x12, `AccentWarning`
- Text: JetBrains Mono 11px Normal, `AccentWarning`

#### 3.3.6 Prompt Input Area
- **Height:** 120px
- **Background:** `BgSurface` (#232730)
- **Border:** 1px top, `Border`
- **Padding:** 12px vertical, 16px horizontal
- **Layout:** Horizontal, **bottom-aligned** (alignItems: end), gap 10px

Contents:
1. **Text input** -- fills remaining width, fills height
   - Background: `BgElevated` (#2C313B)
   - Border: 1px, `Border` (#363C48)
   - CornerRadius: 4px
   - Padding: 10px vertical, 14px horizontal
   - Placeholder: JetBrains Mono 12px Normal, `TextMuted`
   - This is a **multiline TextBox** (AcceptsReturn=true, VerticalScrollBar as needed)
   - Text alignment: top-left

2. **Send button** -- height 32px
   - Background: `AccentBlue` (#5B9CF6), cornerRadius 4
   - Padding: 0 vertical, 16px horizontal
   - "Send" -- JetBrains Mono 12px SemiBold, white (#FFFFFF)

3. **Queue button** -- height 32px
   - Background: `BgElevated` (#2C313B), cornerRadius 4
   - Border: 1px, `Border`
   - Padding: 0 vertical, 16px horizontal
   - "Queue" -- JetBrains Mono 12px Medium, `TextSecondary`

### 3.4 Inspector Panel (Right)

- **Width:** 300px fixed
- **Height:** fill
- **Background:** `BgSurface` (#232730)
- **Border:** 1px left, `Border`
- **Layout:** Vertical

#### 3.4.1 Inspector Tab Bar
- **Height:** 36px
- **Background:** `BgSurface` (#232730)
- **Border:** 1px bottom, `Border`
- **Padding:** 0 vertical, 16px horizontal
- Same tab styling as center panel tabs (active = BgElevated + 2px blue bottom border)
- Tabs: "Queue" (active), "Hooks" (inactive)

#### 3.4.2 Inspector Body
- **Padding:** 16px all
- **Layout:** Vertical, gap 12px

**Empty state** (centered vertically and horizontally):
- Lucide `inbox` 32x32, `TextMuted`
- "0 items" -- JetBrains Mono 12px Normal, `TextMuted`
- "Queue is empty" -- JetBrains Mono 11px Normal, `TextMuted`

**Clear button** (bottom-right):
- `BgElevated` bg, padding [4, 10]
- "Clear" -- JetBrains Mono 11px Medium, `TextTertiary`

### 3.5 Status Bar (Bottom)

- **Height:** 28px
- **Background:** `BgSurface` (#232730)
- **Border:** 1px top, `Border`
- **Padding:** 0 vertical, 16px horizontal
- **Layout:** Horizontal, center-aligned, gap 16px

Contents:
- "AcmeCorp:" -- JetBrains Mono 10px Medium, `TextTertiary`
- "5h:" `TextMuted` + "71%" `AccentBlue` SemiBold (gap 4)
- "7d:" `TextMuted` + "31%" `TextSecondary` Medium (gap 4)
- "Spent:" `TextMuted` + "$71" `AccentWarning` Medium + "/ Limit: $200" `TextMuted` (gap 4)
- Spacer
- "Build: 073142" -- JetBrains Mono 10px Normal, `TextMuted`

---

## 4. Icon Library

All icons use the **Lucide** icon set (https://lucide.dev) except one Feather icon.

| Icon name | Size | Location |
|-----------|------|----------|
| menu | 16x16 | Title bar hamburger |
| minus | 14x14 | Window minimize |
| square | 14x14 | Window maximize |
| x | 14x14 | Window close |
| plus | 14x14 | New Session button |
| circle-check | 10x10 | Session ID tag |
| settings | 16x16 | App bar |
| help-circle (Feather) | 16x16 | App bar |
| refresh-cw | 12x12 | Session bar refresh |
| message-square | 12x12 | Messages badge |
| info | 12x12 | Notification bar |
| chevrons-right | 14x14 | Tab bar collapse |
| inbox | 32x32 | Inspector empty state |

**WPF implementation:** Use a Lucide icon font or SVG path data. The `Segoe Fluent Icons`
font can substitute where exact matches exist.

---

## 5. Interaction States (Design Intent)

These states are not shown in the static design but should be implemented:

### Hover States
- **Buttons:** Lighten background by ~10% or add subtle brightness
- **Session cards:** Show subtle background change on hover
- **Tabs:** Show background on hover for inactive tabs
- **Icon buttons:** Brighten icon color to `TextSecondary` on hover

### Active/Pressed States
- **Buttons:** Darken background slightly on press
- **Session cards:** Already styled (active card has lighter bg + border)

### Focus States
- **Text input:** Change border to `AccentBlue` on focus
- **Buttons:** Show 2px outline in `AccentBlue` with 2px offset

---

## 6. Layout Measurements Summary

| Element | Width | Height | Notes |
|---------|-------|--------|-------|
| Window | 1440 | 900 | Resizable, these are default |
| Title Bar | fill | 36 | |
| Sidebar | 240 | fill | Fixed width |
| Center Panel | fill | fill | Stretches |
| Inspector | 300 | fill | Fixed width |
| Status Bar | fill | 28 | |
| App Bar | fill | 36 | In center panel |
| Session Bar | fill | ~75 | Fits content, in center panel |
| Tab Bar | fill | 36 | In center panel |
| Terminal Area | fill | fill | Takes remaining space |
| Notification Bar | fill | 28 | In center panel |
| Prompt Input | fill | 120 | In center panel |
| New Session Btn | fill | 36 | In sidebar |
| Session Card | fill | ~85 | Fits content |
| Send Button | auto | 32 | |
| Queue Button | auto | 32 | |

---

## 7. Resizing Behavior

- **Sidebar (240px):** Fixed width. Could be collapsible in future (chevrons-right icon suggests this).
- **Inspector (300px):** Fixed width. Could be collapsible.
- **Center Panel:** Stretches to fill remaining space.
- **Terminal Area:** Grows/shrinks vertically with window resize.
- **Prompt Input Area:** Fixed 120px height. The multiline TextBox inside fills the available space.
- **Status Bar, Title Bar, App Bar, Tab Bar:** Fixed heights, stretch horizontally.
- **Session list:** Scrollable vertically when sessions exceed available space.

---

## 8. Font Installation Requirements

The design requires these fonts to be installed or embedded:
- **Inter** -- https://fonts.google.com/specimen/Inter
- **JetBrains Mono** -- https://www.jetbrains.com/lp/mono/

Bundle both as embedded resources in the WPF project. Reference with:
```xml
<FontFamily x:Key="FontHeading">pack://application:,,,/Fonts/#Inter</FontFamily>
<FontFamily x:Key="FontMono">pack://application:,,,/Fonts/#JetBrains Mono</FontFamily>
```

---

## 9. WPF-Specific Notes

1. **Custom window chrome:** Use `WindowChrome` to remove the default title bar and render
   the custom TitleBar. Set `ResizeMode="CanResize"` and `WindowStyle="None"`.

2. **ConPTY terminal:** The terminal area hosts an external terminal. Use
   `Microsoft.Terminal.Wpf` NuGet package or a `HwndHost` wrapping a ConPTY instance.
   Do NOT try to replicate the terminal content in XAML.

3. **Separator/border lines:** Use 1px `Border` elements or `BorderThickness` on containers.
   Where the design shows a border on only one side (e.g., bottom only), use
   `BorderThickness="0,0,0,1"`.

4. **CornerRadius:** Only used on: buttons (4px), session cards (4px), badges (3px),
   input field (4px), CC logo (6px). Everything else is sharp corners (0px).

5. **Grid splitters:** Consider adding `GridSplitter` between sidebar/center and
   center/inspector for user-resizable panels in the future.

6. **Text rendering:** Set `TextOptions.TextFormattingMode="Display"` and
   `TextOptions.TextRenderingMode="ClearType"` for crisp small text on Windows.
