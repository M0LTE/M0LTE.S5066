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

- P3-0 is merged (#27). P3-1 (#20) and P3-2 (#21) are with Opus implementers, in parallel.
- Agents get standard briefs from /home/tf/src/s5066-research/scripts/implementer-brief.md and reviewer-brief.md. The coordinator's prompt only names the item, issue and PRs.
- Merging is one command: `/home/tf/src/s5066-research/scripts/merge-pair.sh <conformance_pr> <code_pr>` merges the conformance PR, repins the submodule, waits for CI, merges the code PR and cleans up.
- Modem 0.5.0 is merged in M0LTE.Ms110d. Tagging v0.5.0 to publish is blocked by the classifier and is waiting on Tom (he runs the tag, or adds an allow rule). The SNR estimator (0.6.0) comes next in that repo, and is not started.
- Lesson: in the main checkout, run `git submodule update` before any `git add -A`.
- Lesson: an interrupt can stop background agents silently. Check ListAgents before claiming anything is running.

## Next

- Phase 3 items run in the order of core-architecture.md section 9, each with a conformance PR first, an Opus implementer for state machines, and a fresh Opus reviewer.
