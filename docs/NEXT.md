# What I'd change next

Ordered by how much it affects daily use, not by how interesting it is to build.

---

## 1. The panel almost never appears — **fixed**

**The problem.** In `peek` mode the overlay needs one of two triggers, and during real work
neither fires:

| Trigger | Condition | Why it doesn't fire |
|---|---|---|
| Peek | the **matched app** changes | every unconfigured window falls back to `global`, so Freebuff → terminal → Explorer is not an app change |
| Idle reveal | 30 s with zero input | typing or mouse movement resets the timer constantly |

Measured: one peek at startup, 6 s visible, then hidden for the rest of a 12-hour session.
The process was healthy the whole time — correct styles, sampling backdrops, animating opacity
— but from the outside it looked like it wasn't running at all.

**The fix (implemented).** Decouple "something is worth showing" from "the app changed":

- Peek on **foreground window** change, not matched-app change. Any switch to a different
  window (not just a different *app*) is a context switch where the shortcut list is relevant.
  In the current code that is one comparison: keep the window handle alongside `_lastAppId`.
- Add a **slow ambient flash**: after N seconds since the last reveal, show for 6 s even if
  nothing changed. This is what makes discovery work — the point of the app is to show you
  shortcuts you didn't know existed, which requires showing them unprompted at some cadence.
- Consider raising `peek_seconds` from 6 to 8–10; 6 s is short for reading eight rows of
  unfamiliar text.

**Suggested defaults:** peek on any foreground-window change; re-peek every 90 s if nothing
has been revealed since; keep the 30 s idle reveal.

## 2. Discovery entries have no visual cue

The `NEW` badge was removed with the card, and nothing replaced it. The rotation logic still
prefers unseen entries, but you can't tell which row is the new one — so the core idea of the
app ("here's one you don't know") is invisible.

Cheap fix that survives the borderless look: dim the action text for already-seen entries and
keep the unseen one at full brightness, or prefix it with a `+` glyph. One property on the row
record, no layout change.

## 3. Rotation happens while nobody is watching — **fixed**

`Rotation.SlotFor(elapsed)` runs off wall-clock time since launch, so a slot boundary at
09:03 is consumed by a hidden panel and wasted. Rotation should advance on **reveal**, not on
elapsed time: increment the slot each time the panel becomes visible, so every appearance is
fresh and you see the whole list over a few appearances instead of every 3 minutes forever.

## 4. `peek` and `ambient` are the only two modes, and both extremes are unsatisfying

Right now the choice is "invisible almost always" or "always on at 0.90". A third mode would
probably suit actual use better: **on-demand** — invisible until the cursor rests near a screen
edge for ~400 ms, then it appears at that edge (an edge-hover reveal, the way macOS hot corners
work). No hotkey to remember, no permanent panel, and the reveal is deliberate rather than
random. This is the feature I'd build after fixing #1.

## 5. No settings UI, and the tray menu is thin — **done**

Config files and MCP are the intended surface and that's fine, but the tray icon is the only
discoverable entry point and it currently offers little more than quit/toggle. Worth adding:
"Show now", "Open config folder", "Reload config", and a pointer to the shortcut list. Ten
minutes of work, removes most of the "how do I use this" friction.

## 6. Per-monitor and multi-monitor support

`OverlayGeometry` clamps to `SystemParameters.WorkArea` — the **primary** monitor only. On a
multi-monitor setup the panel will dodge within one screen while the cursor is on another.
`MonitorFromWindow` + `GetMonitorInfo` per monitor would fix it; the geometry is already
parameterised by a work-area rect, so this is mostly plumbing.

## 7. Backdrop sampling only looks at a ring

`BackdropSampler.Sample` reads a ring around the panel (to avoid measuring itself). It's close
enough for halo sizing, but it does mean the panel adapts to the *neighbourhood* rather than the
exact pixels behind it. Sampling the panel's own rect while it is hidden (`Visibility.Hidden`
means the pixels behind it are just the desktop) would be strictly better and is symmetric with
how the dodge already works — both only run while invisible.

## 8. Unaddressed corners worth knowing about

- **Lock screen / display sleep** — not handled specially. On resume the panel keeps its last
  position and opacity, which is fine, but the idle timer may read oddly for a moment.
- **DPI changes** — handled by converting through `CompositionTarget.TransformFromDevice`, but
  only at the moment of the dodge; moving the panel between monitors with different scaling
  mid-session isn't tested.
- **Startup with Windows** — not implemented. A shortcut in `shell:startup` is the manual
  workaround.
- **Accessibility** — the overlay is invisible to screen readers by design (`WS_EX_TRANSPARENT`,
  no automation peers). That's correct for an ambient widget, but it means the only accessible
  path to the same data is the MCP server or the config files.
- **Code signing** — unsigned executable; SmartScreen will warn on a fresh machine.

## 9. Engineering follow-ups

- **Tests are one 600-line file.** Splitting by concern (Matcher / IdlePolicy / Geometry /
  Rotation / Config / Mcp) would make failures self-describing. No behaviour change.
- **No CI.** A GitHub Actions workflow running `dotnet test` on `windows-latest` is ~15 lines
  and would catch a WPF build break before a push does.
- **`SHORTCUTSCOUT_TRACE=1` writes unbounded to `%TEMP%`.** One line every 500 ms while the
  panel is hidden. Fine for a debugging session, worth a size cap before it's left running.
- **The trace log is the only telemetry.** Couldn't hurt to log one line per *reveal* with the
  reason (`peek` / `idle` / `toggle`), which is exactly what would have made defect #1 obvious
  within a day rather than needing a forensic pass.
