# ShortcutScout — Specification

## Core purpose
1. Windows app that detects the currently focused/foreground window and shows keyboard shortcuts *for that app*.
2. It's general, not one app: Zellij when in Zellij, Claude Code, Devin, ChatGPT (especially), Chrome, etc.
3. The point is discovery — surfacing shortcuts you *don't even know exist*, not just reminding you of forgotten ones.
4. The list should *gently change over time* — rotating in lesser-known shortcuts so you steadily discover more.

## Overlay behavior (the technically hard part)
5. Transparent overlay, always-on-top, click-through, completely transparent when off.
6. It should *run away from your cursor* — avoid sitting under the mouse / overlapping it.
7. It should be aware of the focused window (process/exe/title) without stealing focus itself.
8. Shortcuts content is secondary — nailing the transparency, awareness, fade, click-through, and cursor avoidance is the real challenge.
9. **It's mostly automatic, not hotkey-driven:** auto fading/behavior drives appearance most of the time. The toggle hotkey is only occasional, for when you want to hide it completely.
10. Numbers become *more* visible after you leave the computer alone for 30 seconds (idle brightening).

## Config & agent
11. Filled via configuration files (human-editable per-app shortcuts).
12. Easy MCP / agent integration to configure the shortcuts.

## Priority apps
13. Nail Devin and ChatGPT here — notably these are web apps (in Chrome / desktop shell), so detection can't be pure process-name matching.

## Chosen stack (researched)
- **WPF / C#** — best native transparency, click-through (`WS_EX_LAYERED` + `WS_EX_TRANSPARENT` + `WS_EX_TOPMOST`), smooth opacity animation, `GetForegroundWindow` awareness without stealing focus, `GetLastInputInfo` idle (30s), `GetCursorPos` cursor avoidance, `RegisterHotKey` toggle, tray via `Shell_NotifyIcon`.
- Research notes: transparency ≠ click-through (separate Win32 styles); Electron heavy + integrity issues; Tauri Windows alpha ignored; tkinter whole-window alpha only.
