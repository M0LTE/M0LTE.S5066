# Coordinator handover

How the coordinating session runs this project. It is written so that a fresh session can resume from this file plus `docs/status.md` and the open issues.

## Rules of engagement (Tom, 2026-11-07)

- The effort is autonomous; the coordinator decides. Tom wants minimal output: direct questions only, sent by mobile push notification. He gets check-ins after the SNR estimator validation, after the CSMA-mode measurements, and before anything goes on air.
- The coordinator writes no code; sub-agents do. Keep the coordinator's own context small.
- Models: Opus 5.5 for reviewers and protocol state machines; Sonnet 5.5 for codecs, scaffolding, tests and docs; Fable 5.1 for judgement (ambiguity rulings, SNR estimator and rate algorithm design, CSMA results); Haiku 5.5 for transcription jobs, always reviewed.
- Heavy sweeps run on AWS spot instances requested from Tom (spot-broker skill); only small compressed results come back.
- M0LTE.Ms110d changes stay additive. Its consumers are pdn-soundmodem and pdn-mailcast.

## Work loop

1. Each work item is an issue labelled with its phase.
2. An implementer agent works in a git worktree: `git -C <repo> worktree add <repo>/.claude/worktrees/<name> -b <branch> origin/main`. It commits as `git -c user.name="Tom Fanning" -c user.email="tom@fann.ing" commit`, with the message ending in a Co-Authored-By line for its model. It opens a PR and does not merge.
3. A fresh Opus reviewer is given the PR, the matrix rows and the annex pages, never the author's summary. It fixes things on the branch and comments on the PR.
4. The coordinator settles any open rulings (writing "Coordinator ruling <date>" into the matrix row's notes), then squash-merges with `gh pr merge N --squash`. It then removes the worktrees, deletes the local and remote branches, and updates `docs/status.md`.

## Where things are

- The plan is `docs/plan.md`. The matrix and corpus are in M0LTE/s5066-conformance (a submodule at `conformance/` from P1a).
- The Ed4 annex texts are local only, in `/home/tf/src/s5066-research/ed4/`. Research notes are in `/home/tf/src/s5066-research/notes/`.
- The Claude Doc snapshot of the plan is https://claude.ai/artifact/Vmj59EyJtYbabb8yziDKpq. It is not maintained; the repo is the master.

## In flight (update when this changes)

- Phase 3 is complete (P3-0 to P3-7 merged; 971 tests; corpus 0.7.x). Phase 4 issues are #39 onwards (P4-1 to P4-7), in the order of core-architecture.md section 9.
- #35 fix merged (#46, corpus 0.8.0, address_size setting removed). P4-1 merged (#47, corpus 0.9.0). P4-2 (#40, ARQ data path) is with an Opus implementer; interp ids C-1120 to C-1149 and A-0310 to A-0319 are reserved. Next: P4-3 to P4-6, then P4-7 (T3 ARQ).
- Modem: 0.5.0 is published. 0.6.0 (the SNR estimator) is merged in M0LTE.Ms110d (merge commit 5862646b), but the v0.6.0 tag is blocked by the classifier: Tom's tag permission covered 0.5.0 only. Asked Tom for standing permission. Follow-up #36 (NewPreamble trailing probes) is for 0.6.1.
- Phase 7 inputs: #38 (reception-window latency allowance); the SNR estimate tracks post-equaliser SNR, so calibrate block error rate against the estimate per WN (M0LTE.Ms110d docs/snr-calibration.md).
- Agents get standard briefs from /home/tf/src/s5066-research/scripts/implementer-brief.md and reviewer-brief.md. Merging uses /home/tf/src/s5066-research/scripts/merge-pair.sh <conf_pr> <code_pr>. When two items run in parallel, reserve separate interp id ranges, and expect an integration step for whichever merges second.
- Lessons: run `git submodule update` before any `git add -A` in the main checkout; merge-pair.sh removes only the merged branches' worktrees; an interrupt can stop background agents silently (check ListAgents); stop finished agents' leftover background work with TaskStop.

## Next

- Carry-overs for P4-1: the CAS must handle the DTS reject primitives (D_UNIDATA_REQUEST_REJECTED and the expedited one). Also record the unbind ruling (A-0260). C-1043 overestimates end time by up to one block; that is safe, and can be revisited for turnaround in phase 7.

- Phase 3 items run in the order of core-architecture.md section 9, each with a conformance PR first, an Opus implementer for state machines, and a fresh Opus reviewer.
