# Stack sweep runbook (P7-8)

`tools/s5066-sweep` runs two whole nodes against each other over a simulated HF channel and measures what the stack delivers: goodput, retransmissions, transmissions per rung, links lost, the gap between bursts and the EOT overshoot (issue #66). It is the T4 harness of core-architecture.md section 9 (P7-8), used for the fixed-rate baselines, the P7-9 tuning rounds and the P7-10 curve. It is a statistical tool, not byte-exact work.

## What one run is

- Two `Node`s (0.0.1.1 and 0.0.1.2), each with an in-process MS110D modem (M0LTE.Ms110d), joined by one continuous Watterson channel per direction (M0LTE.HfChannel: AWGN, Poor or Moderate, optionally the SSB band-pass on both radios), on a virtual clock in 20 ms steps. Inputs reach each core in the host's order (core-architecture.md 12.3, 12.4), and modem events follow the rules of 12.5 and 12.6. A run goes as fast as the two demodulators and repeats exactly for a seed.
- Traffic: node a sends bulk ARQ U_PDUs of 2048 bytes to node b as fast as flow control allows; node b sends a 100-byte ARQ chat message to a every 60 s from 30 s.
- Both nodes take the corpus `defaults.json`, then `tools/s5066-sweep/sweep-node.json` (since P7-9c: 8 contention slots of 3 s, LBT 2 s after DCD and 3 s after VDCD, transmissions of at most 30 s, so a derived answer wait of 57 s; the reasons are in its comments; P7-8 to P7-9b used the bench's 4 slots of 1 s and VDCD 0.5 s), then the run's `drc.default_speed` and `drc.table`.
- Configurations: `fixed:wn6-long` is the fixed-rate baseline, an empty `drc.table` plus `drc.default_speed` (design shape item 6, open question 13). `adaptive:<table>[@wnN-il]` loads a `drc.table` file (a path, or a name in `tools/s5066-sweep/tables/`) and optionally a start rung. `tables/synthetic-test.json` is made up, for building and testing only; never publish a result run on it.
- SNR reference, as P7-5: the set SNR is the on-air signal power (after the sending radio's passband, with the SSB band-pass) over the noise in 3 kHz at the receiving antenna. Every burst is scaled to the mean on-air power over all 36 rungs, so every rung meets the set SNR.
- Goodput counts bulk bytes delivered to b's client after the warm-up, over the rest of the run.

## Commands

```
dotnet run -c Release --project tools/s5066-sweep -- <command> [options]
```

- `run --channel poor --snr 18 --config fixed:wn4-long [--duration-s 600] [--timeline 1] [--json r.json]`: one run, with a summary; `--timeline 1` prints every transmission and link change.
- `plan --profile baseline`: the runs of a sweep and its size.
- `sweep --profile baseline --out DIR [--workers N] [--shard i/n]`: runs a sweep. One JSON file per run in `DIR/runs/`, written when the run finishes; `DIR/plan.json` fixes the grid; `DIR/progress.log` has one line per run with the speed and the time to go.
- `merge --profile baseline --out DIR`: writes `DIR/aggregate/`: `curve-<channel>.tsv` (per set SNR and configuration over the seeds: goodput mean, minimum and maximum, retransmission %, links lost as each node saw them and summed over both nodes, chat delivered and latency, transmissions per rung), `baseline.tsv` (per channel the fixed rung with the highest goodput summed over the sweep, open question 14, and each point's best fixed rung), `turnaround.tsv`, `overshoot.tsv`, `merge.json` (missing and failed runs) and a copy of `plan.json`.
- `realtime --channel awgn --snr 30 --config fixed:wn6-short --duration-s 300`: the real-time cross-check. Two `s5066d` processes over UDP audio with the same channel in the audio path, the same traffic over TCP SIS, then the same settings on the virtual clock; prints both and compares goodput and the gaps. Needs a quiet machine; it runs in real time.

Grid options replace a profile's parts: `--channels awgn,poor-ssb --snrs 0:30:3 --configs fixed:wn2-long,fixed:wn6-long --seeds 1,2,3 --duration-s 1800 --warmup-s 60 --table <name or path>` (the table for every adaptive config). A sweep's `merge` takes the same options as its `sweep`.

Resume: run the same `sweep` command again. Finished runs are skipped, a run that was killed starts again, and a failed run (`runs/<id>.failed`) is retried. A different grid, node file, table or build in the same `--out` is refused.

Shards: `--shard i/n` takes every n-th run from i, so n boxes (or n processes) can split one plan; copy every `runs/` directory into one before `merge`.

## Profiles

| Profile | Grid | Runs | Channel-hours |
|---|---|---|---|
| `smoke` | AWGN and Poor, 18 dB, `fixed:wn4-long` and `adaptive:synthetic-test`, seed 1, 240 s | 4 | 0.27 |
| `baseline` | AWGN, Poor, Poor with SSB band-pass; 0 to 30 dB in 3 dB steps; fixed WN 1 to 8 and 13 at Long; seeds 1 to 3; 30 minutes | 891 | 445.5 |
| `adaptive` | the same channels and points, one adaptive table (`--table`), seeds 1 to 3, 30 minutes | 99 | 49.5 |

## Tests

`tests/M0LTE.S5066.T4.Tests/StackSweepTests.cs`. CI runs one 90 s run twice (it links, delivers and repeats exactly for a seed) and checks sharding, resume and the merge on stand-in results. The smoke profile through the real simulation runs only with `S5066_LONG_TESTS=1`:

```
S5066_LONG_TESTS=1 dotnet run -c Release --project tests/M0LTE.S5066.T4.Tests -- -class M0LTE.S5066.T4.Tests.StackSweepTests
```

## Speed

Measured on studybox (8 cores, 2026-11-08), Release, both nodes' demodulators counted: 0.15 to 0.26 core-seconds per channel-second for single runs on a quiet box, 0.35 to 0.48 with four workers while another sweep shared the box. Fading and the SSB band-pass cost a little more than AWGN. A run uses about 100 MB (peak resident size, runtime included).

For planning take 0.3, and 0.5 as the pessimistic figure. That makes the `baseline` profile about 135 to 225 core-hours, not the 700 of the calibration plan (whose estimate came from the modem's own SNR sweep, before this was measured), and the `adaptive` profile about 15 to 25.

## On a 192-core spot instance

The coordinator requests the instance (spot-broker); nothing here touches AWS. On the box (Amazon Linux 2023, x86_64, 192 cores):

```
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
git clone --recurse-submodules https://github.com/M0LTE/M0LTE.S5066 && cd M0LTE.S5066
git checkout <full commit SHA> && git submodule update --init
dotnet publish tools/s5066-sweep -c Release -o ~/sweep
mkdir -p ~/out && cd ~/sweep
for i in $(seq 0 46); do nohup dotnet s5066-sweep.dll sweep --profile baseline --out ~/out/baseline --workers 4 --shard $i/47 > ~/out/shard$i.log 2>&1 & done
```

That is 47 processes of 4 workers (188 runs at once). Separate processes rather than one process with 188 workers, because the tool runs the workstation garbage collector, which stops every thread of a process while it collects.

Progress, with a tiny command: `ls ~/out/baseline/runs/*.json | wc -l` (runs finished, of 891), or `tail -qn 1 ~/out/baseline/progress-*.log | head -3`. A finished shard prints `[sweep] done` at the end of its `~/out/shard<i>.log`; `grep -L "sweep\] done" ~/out/shard*.log` lists the ones still going. If the instance is interrupted, a new one runs the same commands and skips the finished runs found in `~/out/baseline/runs` (copy them across first, a few MB). A process that dies can be restarted alone with its `--shard i/47`.

When it is done:

```
cd ~/sweep && dotnet s5066-sweep.dll merge --profile baseline --out ~/out/baseline
tar cJf ~/baseline.tar.xz -C ~/out/baseline aggregate runs
```

Copy back only `baseline.tar.xz` (the per-run JSON compresses to a few MB; the aggregates alone are tens of KB). Nothing else on the box is needed.

**Expected run time.** The `baseline` profile is 891 runs of 30 minutes of channel, about 5 waves of 188. At 0.3 core-seconds per channel-second a run takes 9 minutes, so the whole profile about 45 minutes; at 0.5, about 75 minutes. Allow 1.5 hours. An `adaptive` round (99 runs, one wave) takes 10 to 15 minutes, and runs on our own 12-core box in about 1.5 to 2.5 hours.

## First measurements (2026-11-08, made-up table, short runs)

These come from short development runs on studybox (300 s per point, one seed), not from a sweep. They are for P7-5 and P7-9 to check against the full baselines.

- **Gap between bursts.** The answer to a transmission starts a median 7.9 s (AWGN) to 8.9 s (Poor with SSB band-pass) after it ends, over WN 1, 2, 4, 6, 8 and 13 Long and WN 6 Short at 30 dB; on WN 6 Short alone, a median 2.3 s. A sender's next burst after its own (the gap P7-5 assumed to be 6 s) ranges from 0.5 s to 110 s, median 2.5 to 40 s depending on the rung: on Long rungs a sender often goes again 0.5 to 2.5 s after its own burst. In every case the gap is far above the well-under-a-second decorrelation of the fading, so P7-5's 6 s does not change what it measures.
- **EOT overshoot (issue #66).** The receiver's predicted end is late by about one block of the received rung, every time: 0.55 to 1.0 s on WN 6 Short, 7.0 s on WN 4 Long, 7.8 to 8.0 s on WN 6, 8 and 13 Long, 10.5 s on WN 1 and 2 Long.
- **Starvation after a collision.** Because of that overshoot the receiver waits about one block plus 0.5 s after each burst, while the sender may go again after its contention slot alone. Once retransmissions are due (each answer wait, 36 s here, runs out during the sender's next 35 s transmission), the bulk sender keeps the channel and the receiver never answers: at 30 dB AWGN, WN 1 and WN 2 Long delivered nothing in 300 s and lost the link, and WN 4 Long retransmitted 70% of its data. This is the core's behaviour with `sweep-node.json`, measured, not avoided; it is for P7-9 (issues #66 and #67). Fixed by PR #98 (issues #66 and #97): the overshoot is now 0.17 to 0.52 s on Long rungs, the answering node gets the channel, transmissions stay within 30 s (34.9 s before on WN 4 Long), and WN 4 Long went from 205 to 683 bit/s with 5/5 chat. WN 1 and 2 Long still deliver no bulk data in 300 s with these 1 s slots: both nodes now contend from nearly the same instant, and a 1 s slot is shorter than the modem's carrier detect, so equal or adjacent draws collide. With 3 s slots WN 2 Long delivers 137 bit/s without collisions.
- **Transmissions longer than `dts.max_transmission_ms`.** With 30 s configured, WN 4 Long transmissions last 34.9 s: the planner fits whole blocks into the budget, then the modem's 32-bit EOM needs one more block when the bytes fill the last block exactly (core-architecture.md 12.6). The derived answer wait assumes the configured length.
- **Real time against the virtual clock.** AWGN 30 dB, WN 6 Short, 300 s: the same goodput (1843 bit/s) and chat count, and the gap before an answer agrees within 40 ms on the mean. Poor with SSB band-pass 30 dB, WN 6 Long, 1200 s: the same number of transmissions (25 from the bulk sender), but the real nodes answer 1.3 s later on the mean (10.8 s against 9.4 s) and deliver 26% less (1006 against 1365 bit/s). That 1.3 s is host latency on long bursts (issue #38), most likely the time to modulate a 30 s burst before it plays (12.6); the virtual clock modulates in no time.

## P7-9b measurements (2026-11-09, studybox, 30 dB AWGN)

After the first-segment cut (P7-9b, corpus 0.32.0). Fixed WN 1 and WN 2 Long, seeds 1 and 2, `sweep-node.json` with the CSMA values shown. Overlaps are pairs of transmissions on air at once.

- **Link drops on WN 1 Long.** With 4 slots of 3 s the Called node drops the link 132 s after making it, before and after the change: that is its first-data wait (three answer waits, 3 x 44 s) running out before the first 2048-byte C_PDU completes, at about 380 bytes per 21 s transmission. With `cas.first_data_timeout_ms` 600000 no 1800 s run lost a link, before or after. The first-data wait on slow rungs is still a P7-9 decision.
- **Before and after**, 1800 s, 4 slots of 3 s, VDCD 0.5 s, first-data wait 600 s: WN 1 Long 37.7 and 56.5 bit/s before (bursts up to 62 s, retransmissions 54 to 57%), 56.5 and 56.5 after (bursts 21.2 s, 23 to 37%). WN 2 Long 131.8 and 113.0 before (3-block bursts of 31.5 s), 131.8 and 84.7 after (seed 2 had 35 overlaps). 30 s fits two Long blocks on WN 1 and 2 (21.2 s); three need 31.5 s.
- **Contention settings**, WN 1 and 2 Long, overlaps per run: 4 slots, VDCD 0.5 s: 4 to 17 (900 s), 11 to 35 (1800 s); 4 slots, VDCD 3 s: 3 to 6 (900 s, 3 s slots), 9 to 14 (1800 s); 8 slots of 3 s, VDCD 3 s: 2 to 3 (1800 s), retransmissions 6 to 10%, chat 29 or 30 of 30. 1 s and 2 s slots with VDCD 3 s gave 3 to 10 overlaps per 900 s run. The first collision of a run starts 0.24 to 0.52 s after the other transmission (equal draws offset by the EOT overshoot), never 1 s or more apart; later overlaps of 3 to 57 s are follow-ons, a node starting while a collided burst it could not lock to was still on air.
