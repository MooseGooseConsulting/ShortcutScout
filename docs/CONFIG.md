# Configuration

Everything is JSON on disk — no database, no registry. Two places:

```
config/
  shortcuts.json      overlay settings + the fallback "global" app
  apps/<app>.json     one file per app (devin, chatgpt, chrome, claudecode, zellij)
```

Edit by hand, through the settings window (**Ctrl+Alt+O** or the tray menu), or with an
agent over MCP (`ShortcutScout.exe --mcp`). All three write the same files.

**Where the folder is found.** `config/` beside the exe, or the nearest one walking up from
it (which is how a dev build under `src/ShortcutScout/bin/…` reaches the repo's `config/`),
then the same search from the working directory. A folder only counts if it holds
`shortcuts.json` or `apps/`, so an unrelated `config/` directory is never adopted.

**A file that won't parse is skipped, not fatal.** The tray shows a balloon naming the file
and the parse error, on startup and on *Reload config*. If `shortcuts.json` itself is broken,
the running overlay keeps the settings from the last good load instead of snapping back to
defaults; the settings window refuses to save over it until it loads again — that includes a
file that is valid JSON but the wrong shape (`"width": "460"`, an array at the top level).
The CLI prints the same errors as `warning:` lines on stderr.

**Where an edit is written.** An app that lives inside `shortcuts.json` (the shipped `global`
fallback) is updated there; every other app is `apps/<id>.json`. An id is also a file name, so
it must be a plain name — no slashes, colons, or leading/trailing dots, and not a Windows
device name (`con`, `nul`, `com1`…). If two files define the same id, the first one loaded wins
and the other is reported as ignored. An edit is applied to the files as they are on disk at
that moment, so hand edits made while an MCP server is running are kept. While `shortcuts.json`
is unreadable, an edit that would create a new `apps/<id>.json` is refused, because the id
might already be defined in the file that cannot be read.

## Overlay settings (`config/shortcuts.json` → `overlay`)

| Key | Default | What it does |
|---|---|---|
| `mode` | `"peek"` | `peek` = hidden while you work; `ambient` = always visible at `active_opacity` |
| `rotate_seconds` | `4` | Seconds between steps while the panel is visible; `0` holds it still |
| `idle_threshold_seconds` | `30` | Seconds of being away before the panel comes up and stays up. `0` disables the idle reveal. Measured as the larger of OS input-idle and pointer stillness — see below |
| `peek_seconds` | `6` | How long one appearance lasts |
| `re_peek_seconds` | `90` | Show again unprompted after this long with nothing revealed. `0` disables it |
| `active_opacity` | `0.9` | Ambient-mode resting opacity (never allowed below the readable floor of `0.54`) |
| `idle_opacity` | `1.0` | Opacity used for idle reveals and peeks |
| `fade_in_ms` | `200` | Fade-in duration |
| `fade_out_ms` | `180` | Fade-out duration (leaving needs less attention than arriving). The window is hidden when the fade finishes |
| `font_size` | `13` | Text size in DIPs |
| `width` | `460` | Overlay width in DIPs |
| `max_rows` | `8` | How many shortcut rows to show at once |
| `keys_column_width` | `0` | Keys column width; `0` derives it from `font_size` (13.5 × font) |

### Why idleness is measured two ways

`GetLastInputInfo` is not trustworthy on every machine. Measured here: it never exceeded **2 s
across 40 s** of a completely untouched desktop, because something resets the OS input timer
without moving the cursor (a jittery pointing device or a KVM/remote helper). Reading it alone
meant `idle_threshold_seconds` could never be reached, so the idle reveal never happened at all.

The overlay therefore also tracks **pointer stillness** (no movement beyond 6 px) and treats the
larger of the two as the idle time. The visible trade-off: typing for 30 s without touching the
mouse also counts as being away, so the panel can appear while you are working at the keyboard.
Raise `idle_threshold_seconds` or set `mode: "peek"` with a large `re_peek_seconds` if that
is annoying.

## Per-app settings (`config/apps/*.json`)

| Key | Meaning |
|---|---|
| `id` | Stable identifier, also the file name |
| `name` | Display name |
| `priority` | Higher wins when several apps match. Title-matched web apps use `1000`, the browser `10`, `global` `0` |
| `process_names` | Process name matches (no `.exe`). **Empty for browser-hosted apps** so they can't hijack every browser window |
| `exe_names` | Executable matches, e.g. `ChatGPT.exe` |
| `title_contains` | Case-insensitive window-title substrings. This is how Devin/ChatGPT inside Chrome are detected |
| `shortcuts[]` | `{ keys, action, discovery, category }` — `discovery: true` marks shortcuts worth surfacing to someone who doesn't know them |

## Reveal rules (what makes the panel appear)

1. **A window comes to the front** — a different foreground window, whatever app it belongs
   to. Rate-limited to once every 4 seconds (`MinPeekGapSeconds`) so alt-tabbing doesn't strobe.
2. **The matched app changes** — catches tab switches: Devin → ChatGPT is a different app
   in the *same* Chrome window, so the window handle never changes.
3. **Nothing has been shown for `re_peek_seconds`** — the periodic reveal, measured from the
   last reveal. Without this the panel can stay hidden for an entire session during steady work.
4. **You step away for `idle_threshold_seconds`** — stays up until you come back.
5. **`Ctrl+Alt+S`** hides everything until pressed again.

Rotation advances **once per appearance**, so every time you see the panel it's a different
slice of the list. It does not tick on a wall clock: the old 3-minute timer rotated while nobody
was looking, which is how the list appeared never to change.

While the panel is up, the rows also step to the next slice every `rotate_seconds` (default 4),
fading in place instead of blinking. `rotate_seconds: 0` holds the list still for the whole
appearance. This is what makes a long peek — and `ambient` mode in particular — keep teaching
instead of freezing on the same eight rows.

**Two-row limitation:** rotation only reorders the `discovery` entries; the core rows are
always taken in the same fixed order (`Rotation.Select` fills two core rows before each
discovery row). So with `max_rows: 2` the panel shows the first two core shortcuts every
time and the slide is effectively invisible — a discovery entry only reaches the screen
when there is room for a third row. Raise `max_rows` (the default is `8`) if you want to
see the rows actually change.

## Things worth being able to vary (not all implemented)

Grouped by how much they'd change daily use. Implemented ones are marked.

**Text and shape**

- `font_size`, `width`, `max_rows`, `keys_column_width` — **implemented**, also in the settings window
- `font_family` — currently Consolas, hard-coded in the XAML
- `line_spacing` / row margin — fixed at `0,2` per row
- `key_color` / `action_color` — fixed neon `#39FF14` / `#8CFF7A`
- `halo_blur` / `halo_opacity` — adapted from the backdrop (4.0–6.5), not user-tunable
- `align_keys` — the keys column is fixed width so actions line up; some people prefer ragged
- `show_categories` — the field exists in the data model but nothing renders it

**Placement**

- `dock` — pin to one of 9 slots instead of always jumping to the far corner
- `placement_strategy` — `furthest` (current), `nearest`, `fixed`
- `margin` / `inset` — 72 px clearance and 20 px screen inset, currently constants
- `multi_monitor` — currently clamps to the primary monitor's work area only
- `keep_position` — remember the last position across restarts (currently resets to top-left)

**Behavior**

- `mode`, `peek_seconds`, `re_peek_seconds`, `idle_threshold_seconds`, `active_opacity`, `fade_*` — **implemented**
- `min_peek_gap_seconds` — **implemented as a constant** (`MainWindow.MinPeekGapSeconds`, 4 s); not yet a config key
- `rotation_every_reveals` — rotate every N appearances instead of every one (not implemented; the slot is a plain counter in `MainWindow`)
- `hide_when_fullscreen` — don't overlay games/videos
- `hide_when_battery` — be polite on battery
- `pause_apps` — per-app opt-out list
- `discovery_highlight` — how a not-yet-seen shortcut is marked (`NEW` badge, dimmer seen rows, a `+` prefix…)
- `start_with_windows` — not implemented; a shortcut in `shell:startup` is the workaround

## Why some defaults are what they are

- `idle_threshold_seconds: 30` — from the original requirement ("numbers become more visible
  after I leave the computer alone for 30 seconds").
- `active_opacity: 0.9` — the value PowerToys ships for its shortcut guide, and comfortably
  above the ~0.54 scrim floor needed for 4.5:1 body text over white.
- `re_peek_seconds: 90` — long enough not to nag, short enough that a session actually shows
  you something you didn't know. Set it to `0` if you only want reveals on window switches.
