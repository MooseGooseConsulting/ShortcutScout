# Open work

Only things that are **not** built. When something here ships, delete it and fold the outcome
into `README.md` (behaviour) or `CONFIG.md` (keys) — a plan that has been executed does not
stay in this file.

Ordered by how much it affects daily use.

---

## 1. Unseen shortcuts have no visual cue

`discovery: true` marks the shortcuts the app exists to surface, and the rotation prefers ones
you haven't seen yet — but nothing on screen says which row is new. When the card was removed
for the floating-text redesign, the `NEW` badge went with it and nothing replaced it. So the
core promise ("here's one you didn't know") is invisible.

Cheap fix that survives the borderless look: dim the action text of already-seen entries and
keep unseen ones at full brightness, or prefix unseen rows with a `+`. One field on
`ShortcutRow`, no layout change.

## 2. No on-demand mode

`peek` and `ambient` are the only options, and both extremes are unsatisfying: invisible almost
always, or always on. A third mode is worth building — **edge hover**: invisible until the
cursor rests near a screen edge for ~400 ms, then it appears at that edge. Same calm model,
deliberate instead of random, and no hotkey to remember.

## 3. Multi-monitor

`OverlayGeometry` clamps to `SystemParameters.WorkArea` — the primary monitor only. On a
multi-monitor setup the panel dodges within one screen while the cursor may be on another.
`MonitorFromWindow` + `GetMonitorInfo` per monitor would fix it, and the geometry is already
parameterised by a work-area rect, so this is mostly plumbing.

## 4. Backdrop sampling only sees a ring

`BackdropSampler.Sample` reads a ring *around* the panel to avoid measuring itself. That's
close enough for halo sizing, but sampling the panel's own rect while it is hidden would be
strictly better — the pixels behind it are just the desktop at that moment, and hiding is
exactly when the dodge already runs.

## 5. Settings the config doesn't expose yet

Listed with rationale in [`CONFIG.md`](CONFIG.md). The ones most likely to matter: font family,
line spacing, key/action colours, halo strength, dock corner and placement strategy,
`rotation_every_reveals`, hide in fullscreen, pause on battery, per-app opt-out, and
`min_peek_gap_seconds` (currently a constant).

## 6. Not built at all

- **Start with Windows** — a shortcut in `shell:startup` is the manual workaround.
- **Code signing** — unsigned, so SmartScreen warns on a fresh machine.
- **Tray menu depth** — Settings / Toggle / Reload / Exit only; "Show now" and "Open config
  folder" (the latter exists in the settings window) would round it out.

## 7. Engineering

- **Tests are one 600-line file.** Split by concern (Matcher / IdlePolicy / Geometry / Rotation
  / Config / Mcp) so failures name themselves. No behaviour change.
- **No CI.** A `windows-latest` workflow running `dotnet test` is ~15 lines and would catch a
  WPF build break before a push does.
- **No VM lane for the live desktop checks.** `tools/verify_placement.ps1`,
  `tools/verify_runaway.ps1` and `tools/measure_idle.ps1` opt in via
  `-AllowCursorControl` (see `CONFIG.md`/`AGENTS.md`), but there is still nowhere
  safe to run them: they need a disposable Windows VM with a pinned DPI and work
  area, so a sweep is reproducible and never lands on somebody's working desktop.
  Until that exists they stay unrun, and the read-only substitute is the trace,
  which logs cursor and placement every tick.
- **`SHORTCUTSCOUT_TRACE=1` writes unbounded** to `%TEMP%\shortcutscout-trace.log`, one line
  every 500 ms while hidden. Fine for a session, worth a size cap before it runs for days.
- **Only reveals are worth logging permanently.** One line per reveal with its reason
  (`window change` / `app change` / `re-peek` / `idle`) is what made the "never appears" defect
  findable at all — that belongs in the normal trace, not behind an env var.

## 8. Known limits that are probably fine

- **Lock screen / display sleep** — no special handling; on resume the panel keeps its last
  position and opacity.
- **DPI changes** — converted through `CompositionTarget.TransformFromDevice`, but only at the
  moment of the dodge; dragging between monitors with different scaling mid-session is untested.
- **Accessibility** — the overlay is deliberately invisible to screen readers
  (`WS_EX_TRANSPARENT`, no automation peers). The accessible path to the same data is the MCP
  server or the config files.
- **Devin/ChatGPT detection** — depends on the window title still containing their domain; a
  site rename breaks it until `title_contains` is updated.
