# Requirements coverage

A point-by-point account of how ShortcutScout meets (or doesn't meet) the 13 points in
[`SPEC.md`](../SPEC.md). Status is deliberately blunt: **Met**, **Partial**, or **Not met**.
Every "Met" has something you can re-run, not just an assertion.

Verified on: Windows 11, .NET 8.0.428, 200% DPI display (2880×1800 physical, 1440×900 DIP).

| # | Requirement | Status |
|---|---|---|
| 1 | Detect the focused window, show shortcuts for *that* app | **Met** |
| 2 | General, not one app | **Met** |
| 3 | Discovery — surface shortcuts you don't know exist | **Partial** |
| 4 | The list gently changes over time | **Met** |
| 5 | Transparent, topmost, click-through, invisible when off | **Met** |
| 6 | Run away from the cursor | **Met** (with a caveat) |
| 7 | Aware of the focused window without stealing focus | **Met** |
| 8 | Nail the mechanics before the content | **Met** |
| 9 | Mostly automatic; hotkey only for hiding | **Met** |
| 10 | More visible after 30s idle | **Met** |
| 11 | Human-editable config files | **Met** |
| 12 | Easy MCP / agent integration | **Met** |
| 13 | Nail Devin and ChatGPT (web apps in Chrome) | **Met** (matcher level) |

---

## 1. Detects the focused window and shows its shortcuts — Met

`Win32.GetForegroundWindow` → window title + process id/name, read on every 500 ms tick
(`ForegroundReader`). `Matcher` scores every configured app and takes the best match.

Evidence: `dotnet test` → `Match_ByProcess_Zellij`, `Match_UnknownProcess_ReturnsNull`.
Live: the overlay rendered "Reopen the last closed tab / Open the command palette" while a
Chrome window was focused, and general shortcuts over a terminal.

## 2. General, not one app — Met

Ships five app configs plus a `global` fallback:

| App | Matching |
|---|---|
| Devin | title contains `devin.ai` / `Devin` |
| ChatGPT | `ChatGPT.exe`, title contains `chatgpt.com` / `ChatGPT` |
| Claude Code | process `claude` / `node`, title patterns |
| Zellij | process `zellij` |
| Chrome | title only — **no** process match |
| global | catch-all, priority `0` |

New apps are a JSON file in `config/apps/`; no code change.

## 3. Discovery of unknown shortcuts — Partial

Implemented as data: `Shortcut.Discovery`, with unseen discovery entries preferred and
rotated (`Rotation.SurfacesDiscoveryItems`, `Rotation_SurfacesDiscoveryItems` in the suite).

**Why partial:** when the card was replaced with bare floating text, the `NEW` badge went
with it. Discovery entries are now visually identical to core entries, so the app surfaces
unknown shortcuts but doesn't *signal* that they're new. The mechanism works; the cue is
missing.

## 4. The list gently changes over time — Met

Rotation is counted in **appearances**, not wall-clock time: `MainWindow.StartPeek` advances
`_rotationSlot` each time the panel is about to appear, and rows are built from that slot
*after* the advance, so the new slice is what fades in. Core entries are interleaved 2:1 with
`discovery` entries (`Rotation_SurfacesDiscoveryItems`, `Rotation_ChangesAcrossSlots`,
`Rotation_NeverExceedsMaxOrRepeatsAnEntry`).

The earlier design rotated every 3 minutes of uptime, spent while the panel was hidden — you
could watch it for an hour and see the same rows. Counting appearances means every look is a
different slice.

**Limit:** rotation is driven by reveals, so it only moves when the panel appears. In `ambient`
mode that means a window switch or a periodic re-peek (`re_peek_seconds`); with periodic
reveals disabled and no window switches, an ambient list is static.

## 5. Transparent, always-on-top, click-through, fully invisible when off — Met

Window styles applied at source and measured on the running process:

```
exstyle = 0x080800A8   WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST
                       | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
```

Re-check with `powershell -File tools/window_state.ps1`. Off = `Opacity 0` **and**
`Visibility.Hidden`, so it isn't merely see-through, it's absent. Transparency and
click-through are separate Win32 concerns and both are set — a transparent window still eats
clicks without `WS_EX_TRANSPARENT`.

The redesign also removed the entire card: there is no panel, header, border or footer, only
green text, so there is no rectangle to be "in the way" while it is visible.

## 6. Runs away from the cursor — Met, with one deliberate caveat

`OverlayGeometry.Place` picks the docked slot **furthest from the cursor** (3×3 grid, 20 px
inset, 72 px clearance, 60 px hysteresis) and never places the panel under the pointer.

Live measurement: panel docked right at `(960,646)`; cursor moved to the right side; panel
relocated to `(20,20)`.

**Caveat:** it only relocates while invisible. Motion onset is the most attention-capturing
change a peripheral widget can make, so dodging happens while unseen and it simply never
appears under the pointer. If the cursor lands on it *during* a peek, it stays put until the
peek ends. That's a deliberate trade, not an oversight — but it does mean "run away from your
cursor" is true at reveal time, not continuously.

## 7. Aware of the focused window without stealing focus — Met

`WS_EX_NOACTIVATE` plus a `WM_MOUSEACTIVATE` guard and no `SetForegroundWindow` call anywhere.
`IsHitTestVisible="False"` keeps WPF from wanting focus too. Verified: `GetForegroundWindow()`
immediately after a reveal still returns the app you were using.

## 8. Nail the mechanics before the content — Met

The hard parts were built and measured before content: layered/click-through window, idle
detection via `GetLastInputInfo`, fade in 200 ms / out 180 ms (leaving needs less attention
than arriving), backdrop sampling off the UI thread, and geometry with hysteresis.
`IdlePolicy` is unit-tested to prove no mode can produce an unreadable half-opacity
(`NoMode_ProducesAnUnreadableMidOpacity`).

## 9. Mostly automatic; the hotkey is only occasional — Met

`peek` mode (the default) hides the panel while you work, reveals it in four situations, and
`Ctrl+Alt+S` exists only to make it go away entirely:

| Trigger | Detail |
|---|---|
| Foreground **window** change | rate-limited to one per 4 s (`MinPeekGapSeconds`) so alt-tabbing doesn't strobe |
| **Matched app** change | catches tab switches — Devin → ChatGPT is a new app in the same Chrome window, so the handle never changes |
| **Periodic re-peek** | `re_peek_seconds` (default 90 s) measured from the last reveal; `0` disables it |
| **Idle** | `idle_threshold_seconds` (30 s) of no input |

This requirement was failing badly before: a peek required the *matched app* to change, and
every unconfigured window falls back to `global` — so Freebuff, the terminal and Explorer
were all "the same app" and nothing ever revealed. Measured over a real session: one peek at
startup, `opacity=1.00` for 6 s, then `target=0.00` for twelve hours, with a healthy process
and a correctly-styled window throughout. From the outside that is indistinguishable from
"not running", which is exactly how it was reported.

Re-measured after the fix, with `re_peek_seconds` set to 10:

```
22:49:16 peek started (window change) slot=1
22:49:22 peek started (window change) slot=2
22:49:32 peek started (re-peek)       slot=3   <- 10 s after the previous reveal
22:49:42 peek started (window change) slot=4
```

The re-peek fires at the configured interval, not every 4 s — an earlier version left
`_lastRevealAt` at its initial value, which made `RePeekDue` true permanently and turned the
4 s floor into a strobe.

## 10. More visible after 30 s idle — Met

`IdlePolicy.OpacityFor`: `idle >= idle_threshold_seconds (30)` → `idle_opacity` (`1.0`).
`Idle_ThresholdIsExclusiveAt30Seconds` pins the boundary (29 s → hidden, 30 s → full).

Trace from the running app: `idle=31s opacity=1.00`, then `idle=0s opacity=0.92` on return.
Reproduce with `SHORTCUTSCOUT_FORCE_IDLE=1`, since real input can't be faked.

## 11. Human-editable config files — Met

JSON on disk, no database: `config/shortcuts.json` (overlay settings + global app) and one
file per app in `config/apps/`. Malformed files are skipped rather than fatal
(`Config_SkipsMalformedFilesInsteadOfCrashing`), and there's a JSON Schema-shaped contract in
the README. `reload_config` picks up edits without a restart.

## 12. Easy MCP / agent integration — Met

`ShortcutScout.exe --mcp` speaks JSON-RPC over stdio with five tools: `list_apps`,
`list_shortcuts`, `add_shortcut`, `remove_shortcut`, `reload_config`. Writes go through the
same `ConfigStore` as the UI, so an agent edit lands in the same files a human edits.

Covered by `Mcp_InitializeAndToolsList`, `Mcp_AddThenListRoundTripsThroughDisk`,
`Mcp_UnknownMethodReturnsJsonRpcError`.

**Gap:** there's no copy-paste client snippet for any particular agent host in the README, so
"easy" currently means "easy once you know the command is `ShortcutScout.exe --mcp`".

## 13. Nail Devin and ChatGPT, which are web apps inside Chrome — Met at the matcher level

This is why title matching outranks process matching. Both configs have **empty**
`process_names` and `priority: 1000`; generic Chrome has `priority: 10` and matches by title
only. `Matcher` scores `Priority * 1_000_000 + kind + specificity`, so a title match beats a
process match and a high-priority app can't hijack every Chrome window.

Covered by `Match_TitleBeatsProcess_DevinInsideChrome`, `Match_ChatGptInsideChrome`,
`Match_PlainChromeIsNotHijackedByChatGpt`, `Match_HigherPriorityWinsBetweenTitleMatches`, and
`ShippedConfigsMatchPriorityIntent` (which asserts the *shipped* files still encode that
intent, so a config edit can't silently undo it).

**Honest limit:** these are unit tests against realistic titles, plus a live capture on a
Chrome window. I have not driven a real Devin session end to end, so the shipped
`title_contains` strings are believed-right rather than observed-right in that app.
