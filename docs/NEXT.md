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
- **Tray "Show now"** — the menu can hide/show, reload and open the config and log folders,
  but cannot force a peek on demand.

## 7. Engineering

- **Nothing exercises the window itself.** Every test is against `ShortcutScout.Core`; the WPF
  layer (`MainWindow`, `App`, the settings window) is covered only by live runs. The crash
  handlers in particular have never been seen firing — there is no way to make a tick throw
  without a test hook. A `SHORTCUTSCOUT_FAULT=tick` debug switch, in the spirit of
  `SHORTCUTSCOUT_FORCE_IDLE`, would let a live run prove the log-then-give-up path.
- **Near-duplicate test classes.** The split left `ConfigTests`/`ConfigStoreTests`,
  `RevealPolicyTests`/`RevealRulesTests` and `RotationSlotTests`/`RotationTests` side by side
  (old tests moved verbatim, new ones added beside them). Merge each pair when next touched.
- **No VM lane for the live desktop checks.** `tools/verify_placement.ps1`,
  `tools/verify_runaway.ps1` and `tools/measure_idle.ps1` opt in via
  `-AllowCursorControl` (see `README.md`/`AGENTS.md`), but there is still nowhere
  safe to run them: they need a disposable Windows VM with a pinned DPI and work
  area, so a sweep is reproducible and never lands on somebody's working desktop.
  Until that exists they stay unrun, and the read-only substitute is the trace,
  which logs cursor and placement every tick.


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

## 9. Found in the November 2026 review, not fixed

Small, real, and left alone because none of them bites in the shipped configs.

- **A negative `priority` can never match.** `Matcher.Score` folds priority into one integer
  (`priority * 1_000_000 + kind`), and `Match` skips scores `<= 0`. So `priority: -1` — the
  natural way to say "only if nothing else matches" — silently disables the app. Compare
  `(priority, kind, needle)` as a tuple instead.
- **A pure-black backdrop reads as "no sample".** `BackdropStats.Empty` is `(0,0,0,0)`, which
  is also what a black, perfectly flat screen measures as, so that sample is discarded and
  the previous halo weight sticks. Make "no sample" a nullable instead of a magic value.
- **Ambient mode never re-samples the backdrop.** Sampling only runs while the panel is
  hidden, and in `ambient` it never is, so the halo keeps whatever it measured first.
- **The settings window goes stale after *Reload config*.** The model is re-read, but the
  sliders keep showing the values from when the window opened.
- **`.exe` is stripped anywhere in a name**, not just at the end (`Matcher.Norm`), so
  `my.executor` and `myutor` compare equal. Harmless today; `EndsWith` is the fix.

## 10. Feature ideas from the same review

Ordered by value for the effort.

1. **Let an agent tune and debug the overlay over MCP.** The server can edit shortcuts but
   cannot read or change `overlay` settings, and cannot say what the overlay currently thinks
   is focused. `get_settings` / `update_settings` and a `match_window(title, process)` tool
   (pure — it is just `Matcher.Match`) would let an agent answer "why is it showing Chrome's
   list on this tab?" without a human reading the trace.
2. **Remember what you have already been shown.** `Rotation`'s seen-set lives in memory, so
   every restart begins discovery again from the top. Persisting it (a small JSON beside the
   log) is what makes item 1 of this file — the unseen cue — mean "new to *you*", not "new
   since launch".
3. **`--cli match`** — print the foreground window's title/process and every app's score.
   The same question as the MCP tool, for a human at a terminal; today the only way to see a
   match decision is to infer it from which rows appear.
4. **Import shortcuts instead of typing them.** VS Code's `keybindings.json`, a browser
   extension's command list, or "ask an agent to read the app's docs and call `add_shortcut`"
   — a `tools/import_*.ps1` or an MCP prompt template. The five shipped apps are the ceiling
   until adding a sixth is cheaper than hand-writing JSON.
5. **A "learned it" action.** Discovery rows rotate forever. A hotkey or tray action that
   retires the rows currently on screen ("I know these now") turns the overlay from a
   reminder into something that finishes teaching — it needs the persisted seen-set first.
6. **Start with Windows as a tray toggle** (an `HKCU\…\Run` value) rather than the manual
   `shell:startup` shortcut in section 6; with the single-instance guard in place a double
   launch is now harmless.
