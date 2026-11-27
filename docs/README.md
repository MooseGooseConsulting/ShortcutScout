# Docs

These five files used to overlap without saying which one wins. This is that answer.

| File | Authoritative for | Update it when |
|---|---|---|
| [`../README.md`](../README.md) | What the app is, how to run it, and *why* the design is what it is | Behaviour or the run commands change |
| [`../SPEC.md`](../SPEC.md) | The original 13-point brief | Never — it's the historical input, not a status report |
| [`REQUIREMENTS.md`](REQUIREMENTS.md) | Point-by-point status against `SPEC.md`, with re-runnable evidence | Any requirement changes status |
| [`CONFIG.md`](CONFIG.md) | Every config key, the reveal rules, and knobs that don't exist yet | A key is added/renamed, or a rule changes |
| [`NEXT.md`](NEXT.md) | **Open work only** | Something ships — then delete it here and fold the outcome into README/CONFIG |

Rules that keep these from drifting:

- A shipped item does not stay described as a plan. It moves into `README.md` (behaviour) or
  `CONFIG.md` (keys) and is removed from `NEXT.md`.
- `REQUIREMENTS.md` is the only place statuses live, and a status needs evidence you can
  re-run — a test name, a trace line, or a `tools/` script.
- `SPEC.md` is frozen. If a requirement needs to change, change `REQUIREMENTS.md`'s reading of
  it and say why.

Not yet written: a standalone architecture note — currently the design rationale lives in
`README.md`. Contributor conventions (build/test/verify) live in `../AGENTS.md`.
