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

- P1a (#10) is merged. The conformance corpus is tagged v0.1.0 and the submodule is pinned to it.
- P1b (#11) D_PDU codecs, P1c (#12) C_PDU and S_PDU codecs, and P1d (#13) Annex S codec are running in parallel (Sonnet). Each opens a PR in s5066-conformance (its vectors) and one in M0LTE.S5066 (its code, with the submodule on its branch). Merge order: the conformance PR first, then repin the submodule to conformance main, then merge the code PR. Each needs an Opus reviewer first.
- P2 design: draft PR M0LTE/M0LTE.Ms110d#19, under Opus review. After merge, the design's work items 1 to 7 are the phase-2 implementation (C# and C++ level), at version 0.5.0. Consumers do not change until they choose to.

## Next

- After P1a merges, run P1b (#11), P1c (#12) and P1d (#13) in parallel (Sonnet implementers, Opus reviewer each). Each adds T1 vectors to s5066-conformance first, citing matrix row ids.
- Then phase 3 (DTS non-ARQ). It needs the T2 scenario runner, and Opus implementers.
