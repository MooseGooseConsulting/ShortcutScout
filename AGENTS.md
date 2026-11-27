# AGENTS.md — ShortcutScout contributor notes

Project instructions for anyone (human or agent) working on this repo. Keep this file
short; it points at the authoritative docs instead of repeating them.

## What this is

A Windows WPF overlay that shows the focused app's keyboard shortcuts as bare
neon-green floating text, dodging the cursor, revealing on window/app switches,
re-peeks, and idle. See `README.md` for the design rationale and `SPEC.md` for the
original brief (frozen — do not edit).

## Docs map (`docs/README.md` is authoritative)

| File | For |
|---|---|
| `README.md` | What it is, how to run it, why the design is what it is |
| `SPEC.md` | The original 13-point brief — never edit |
| `docs/REQUIREMENTS.md` | Point-by-point status vs SPEC, with re-runnable evidence |
| `docs/CONFIG.md` | Every config key, reveal rules, knobs that don't exist yet |
| `docs/NEXT.md` | Open work only — delete an item when it ships |

A shipped item moves into `README.md`/`CONFIG.md` and leaves `NEXT.md`. Statuses
live only in `REQUIREMENTS.md` and need re-runnable evidence.

## Build / test / run

Requires .NET 8 SDK (`dotnet` is at `C:\Program Files\dotnet\dotnet` on the dev
machine; use the full path in Git Bash).

```powershell
dotnet build ShortcutScout.sln -c Release   # from the repo root
dotnet test ShortcutScout.sln               # 60 tests, all in
                                          # tests/ShortcutScout.Tests/UnitTest1.cs
```

- **Kill `ShortcutScout.exe` before building** (`taskkill //IM ShortcutScout.exe /F`
  in Git Bash) or the build fails with MSB3027/MSB3021 file-lock errors — the running
  exe locks the output.
- Run: `src/ShortcutScout/bin/Release/net8.0-windows/ShortcutScout.exe`
  (or `dotnet run --project src/ShortcutScout`). Kill it when done unless the user
  asked to leave it running.
- Per-tick decisions (opacity, rotation, slide) live in
  `ShortcutScout.Core/OverlayTicker.Decide` so they are unit-testable without a UI;
  keep new tick logic there, not in `MainWindow`.

## Verification conventions

- Trace: `SHORTCUTSCOUT_TRACE=1` logs each tick to
  `%TEMP%\shortcutscout-trace.log`. `SHORTCUTSCOUT_FORCE_IDLE=1` pins the idle
  reveal on for screenshots/tuning. Always kill the app after a traced run.
- **Never run `tools/verify_placement.ps1`, `tools/verify_runaway.ps1`, or
  `tools/measure_idle.ps1` without the user's explicit permission** — they move or
  inject the user's real mouse input (`SetCursorPos` in the first two, `mouse_event`
  in `measure_idle.ps1`), which hijacks their work.
  Prefer read-only verification: user moves the mouse, agent reads the trace or the
  window rect (`tools/window_state.ps1`).
- Display is 200% DPI. The app positions in DIPs, but what a script reports depends
  on the host's DPI awareness: `verify_placement.ps1` calls `SetProcessDPIAware`
  (physical pixels, divide by 2 for DIPs), while `window_state.ps1` does not, so in a
  DPI-unaware PowerShell host its rects may already be scaled — don't divide blindly.

## Git / PRs

- `main` is protected: **every change goes through a PR**, and review threads must
  be replied to and resolved before merging (squash merge, delete the branch).
- Commit messages should say *why*, not just what; prior history is one-line imperative.
- Reply to bot review threads citing the exact commit that fixed them, then resolve
  via `resolveReviewThread` (GraphQL) or the thread UI. Re-audit the unresolved
  count after the final push — new pushes can re-open stale threads.
- Repo: `MooseGooseConsulting/ShortcutScout` (public). No secrets in the repo —
  config is JSON on disk under `config/`, never env vars or keys.
