# Working on M0LTE.S5066

A STANAG 5066 Edition 4 node over the M0LTE.Ms110d modem. Read `docs/plan.md` (the plan) and `docs/status.md` (where things stand) before starting. Work items are GitHub issues in this repo.

## Hard rules

- **No NATO text in any repository.** The Ed4 annexes are local only, at `/home/tf/src/s5066-research/ed4/ANNEX-*.txt` (PDFs alongside). Cite them as `[Ed4 C.4.2.3, p.C-13]` and paraphrase. A quoted phrase of a few words is acceptable only where exact wording matters. Never copy tables, figures or paragraphs.
- **Nothing made up.** Every protocol behaviour traces to a requirement row in `M0LTE/s5066-conformance/matrix/`. If the standard does not say something, write NOT STATED and record our choice as an `interp` row. Never fill a gap from memory.
- **Page numbers are authoritative.** Section numbers in the text extraction are sometimes missing. Check a rendered page (`pdftoppm -f N -l N -png`) when a number or a figure matters.
- **ASCII only**, and no em or en dashes anywhere (code, comments, docs, commits). Use hyphens, commas or new sentences. Markdown prose is not hard-wrapped.
- **Sans-IO core:** protocol code is deterministic state machines with an injected clock and an injected PRNG. No threads, sockets or wall-clock reads in `M0LTE.S5066.Core`.
- **Test-first from the corpus:** byte-exact cases live in `M0LTE/s5066-conformance/corpus/`, so the C++ port can share them.
- **M0LTE.Ms110d has consumers** (pdn-soundmodem, pdn-mailcast). Changes there are additive and follow its `docs/versioning.md`.
- **Keep state durable:** update `docs/status.md` and the issue when an item finishes or stops. Push work in progress on a branch, so another session can pick it up.

## Research notes

These are in `/home/tf/src/s5066-research/notes/`: `sis-addressing.md`, `dts-drc.md`, `cas-mac.md`, `modem-and-tests.md` and `plan-review.md`. They are secondary sources; the annex text wins.
