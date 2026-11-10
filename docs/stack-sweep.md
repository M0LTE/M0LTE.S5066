# Stack sweep runbook (P7-8)

`tools/s5066-sweep` runs two whole nodes against each other over a simulated HF channel and measures what the stack delivers: goodput, retransmissions, transmissions per rung, links lost, the gap between bursts and the EOT overshoot (issue #66). It is the T4 harness of core-architecture.md section 9 (P7-8), used for the fixed-rate baselines, the P7-9 tuning rounds and the P7-10 curve. It is a statistical tool, not byte-exact work.

## What one run is

- Two `Node`s (0.0.1.1 and 0.0.1.2), each with an in-process MS110D modem (M0LTE.Ms110d), joined by one continuous Watterson channel per direction (M0LTE.HfChannel: AWGN, Poor or Moderate, optionally the SSB band-pass on both radios), on a virtual clock in 20 ms steps. Inputs reach each core in the host's order (core-architecture.md 12.3, 12.4), and modem events follow the rules of 12.5 and 12.6. A run goes as fast as the two demodulators and repeats exactly for a seed.
- Traffic: node a sends bulk ARQ U_PDUs of 2048 bytes to node b as fast as flow control allows; node b sends a 100-byte ARQ chat message to a every 60 s from 30 s.
- Both nodes take the corpus `defaults.json`, then `tools/s5066-sweep/sweep-node.json` (since P7-9c: 8 contention slots of 3 s, LBT 2 s after DCD and 3 s after VDCD, transmissions of at most 30 s, so a derived answer wait of 57 s; the reasons are in its comments; P7-8 to P7-9b used the bench's 4 slots of 1 s and VDCD 0.5 s), then the run's `drc.default_speed` and `drc.table`.
- Configurations: `fixed:wn6-long` is the fixed-rate baseline, an empty `drc.table` plus `drc.default_speed` (design shape item 6, open question 13). `adaptive:<table>[@wnN-il]` loads a `drc.table` file (a path, or a name in `tools/s5066-sweep/tables/`: a bare table, a node configuration holding `drc.table`, or a calibrated `drc-table.json` as `{"source": ..., "table": ...}`, for example `conformance/calibration/ms110d-cs-0.6.1/drc-table.json`) and optionally a start rung. `tables/synthetic-test.json` is made up, for building and testing only; never publish a result run on it.
- SNR reference, as P7-5: the set SNR is the on-air signal power (after the sending radio's passband, with the SSB band-pass) over the noise in 3 kHz at the receiving antenna. Every burst is scaled to the mean on-air power over all 36 rungs, so every rung meets the set SNR.
- Goodput counts bulk bytes delivered to b's client after the warm-up, over the rest of the run.

## Commands

```
dotnet run -c Release --project tools/s5066-sweep -- <command> [options]
```

- `run --channel poor --snr 18 --config fixed:wn4-long [--duration-s 600] [--timeline 1] [--json r.json]`: one run, with a summary; `--timeline 1` prints every transmission and link change, each transmission with the last MAX-SPEED and LOW-SPEED its headers carried (the sender's recommendations to the other node).
- `plan --profile baseline`: the runs of a sweep and its size.
- `sweep --profile baseline --out DIR [--workers N] [--shard i/n]`: runs a sweep. One JSON file per run in `DIR/runs/`, written when the run finishes; `DIR/plan.json` fixes the grid; `DIR/progress.log` has one line per run with the speed and the time to go.
- `merge --profile baseline --out DIR`: writes `DIR/aggregate/`: `curve-<channel>.tsv` (per set SNR and configuration over the seeds: goodput mean, minimum and maximum, retransmission %, links lost as each node saw them and summed over both nodes, chat delivered and latency, transmissions per rung), `baseline.tsv` (per channel the fixed rung with the highest goodput summed over the sweep, open question 14, and each point's best fixed rung), `gate.tsv` (the P7-10 gate per channel, set SNR and adaptive configuration: pass where adaptive's mean is strictly above the baseline rung's, or, where that rung is at its measured stack ceiling (its mean within 2% of its highest on the channel), where adaptive is within 2% of that ceiling; plan 12; a point where nothing delivers is not gated; on a channel with the SSB band-pass, rule `ssb`: pass where no link was lost and under 5% of adaptive's transmissions are on rungs that never decode on that channel, over every run of it in the merge, the goodput columns for information only, ruling (d) of 2026-11-09; columns `rule`, `links_lost` and `never_decode_tx_pct` show the inputs), `turnaround.tsv`, `overshoot.tsv`, `merge.json` (missing and failed runs) and a copy of `plan.json`.
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

## P7-9c probe and first-data wait (2026-11-09)

After the MAX-SPEED probe and the derived first-data wait (P7-9c, corpus 0.33.0), studybox, 1800 s per run, seeds 1 and 2, `sweep-node.json` as now (8 slots of 3 s, VDCD 3 s). Before is main e52a02e with the calibrated table without `probe_run`; after is P7-9c with `probe_run` 2.

- **Poor, 18 to 30 dB, adaptive on the calibrated table.** Goodput (bit/s, seed 1 and 2) before and after: 18 dB 1092, 1054 and 1299, 1242; 21 dB 1082, 913 and 1177, 1468; 24 dB 932, 903 and 1158, 1139; 27 dB 1129, 1129 and 1280, 1393; 30 dB 1007, 1064 and 1299, 1525. Mean over the 10 runs 1031 before, 1298 after (+26%). Before, the bulk sender never left WN 6 Medium and below; after, it spends 27 to 65% of its air time on WN 7 Medium, WN 7 Long and WN 8 Long, which carry 45 to 84% of its bytes. Retransmissions 7 to 17% after, 7 to 22% before. Links lost: none after, 2 in one run before (24 dB seed 2). The absolute figures stay far below the rung rates because the turnaround dominates: the bulk sender is on air about 40% of the run, with 25 to 45 s between its bursts. WN 8 Long bursts are often partial on Poor, so its cap keeps bringing the walk back to WN 6 or 7 and the probe climbs again: that cycle is what `probe_run` and the caps tune.
- **WN 1 Long and the first-data wait,** AWGN 30 dB, `cas.first_data_timeout_ms` unset. Fixed WN 1 Long (empty table) still loses its link on both seeds (37 bit/s): with an empty ladder the derived wait is three answer waits by design. A table whose ladder is WN 1 Long alone (the calibrated thresholds cut to that rung) loses no link on either seed (47 bit/s), against 2 links lost and 37 bit/s for the same table on main. So the slow-rung drop is gone wherever a ladder is loaded; the fixed baseline keeps the old wait.

## P7-9d first-data wait and probe trial (2026-11-09)

Studybox, 1800 s per run, seeds 1 and 2, `sweep-node.json` as now, Poor 18 to 30 dB, the calibrated table with `probe_run` 2. Three builds: main 7d0ebdc, the safe first-data wait and minimum hold alone (interp B-0420 and A-0321 revised), and that plus the trial.

- **Safe first-data wait and minimum hold.** Every run identical to main (goodput, retransmissions, links): on these points no link was waiting for its first C_PDU near the old limit, so only the length of a wait that never ran out changed. With the calibrated ladder (WN 1 Long first) and 30 s transmissions the derived wait is now longer than in P7-9c, as the ruling intends.
- **Trial, not kept:** with the probe on, MAX-SPEED moved down on the estimate only after a burst over `rx_error_limit_ppt`. Goodput (bit/s, seed 1 and 2) main and trial: 18 dB 1299, 1243 and 1205, 1271; 21 dB 1177, 1469 and the same; 24 dB 1158, 1139 and 1158, 1083; 27 dB 1281, 1394 and the same; 30 dB 1299, 1525 and the same. Mean over the 10 runs 1298 on main, 1286 with the trial (-1%); no links lost in either. Seven runs did not change at all: on Poor a burst with errors at or under the limit while the estimate's target sits below MAX-SPEED is rare, and where it happened, holding the rung cost more retransmissions than it gained. Corpus-format 6.2 step 6 stays as corpus 0.33.0.

## P7-9 round 2: where the adaptive node lost (2026-11-09)

Round 1 (spot box, main 719123f, 8 slots of 3 s, VDCD 3 s, seeds 1 to 3) had the adaptive node 20 to 40% below fixed WN 8 Long, the per-channel baseline (open question 14), at AWGN 15 to 30 dB and Poor 18 to 30 dB. Diagnosed from the round 1 per-run JSON and `--timeline 1` reproductions on studybox (same seeds; the round 1 runs reproduce exactly). Every figure below is the mean over seeds 1 to 3 at 1800 s, bit/s. A change of contention order makes a run diverge, so single seeds move by 10 to 20% either way; read differences under about 10% as noise.

**Where the time goes, even at the top rung.** A WN 8 Long transmission is 3 blocks (23.8 s, 18.4 kB): a fourth would pass the 30 s limit. A cycle is about 50 s: the bulk sender on air 45% of the run, the acknowledging node 15% (one Long block, 8.4 s, per acknowledgement, since LOW-SPEED is a Long rung), and 40% idle in the turnarounds (VDCD wait plus 0 to 7 slots of 3 s, both ways). Fixed WN 8 Long reaches 2175 bit/s that way at 18 dB and above, a third of the rung's rate, and so does the adaptive node once it is there: the plateau is the stack's, not the estimate's. Under the 30 s limit the Long rungs also waste part of each transmission: WN 7 Long carries 13.8 kB in 2 blocks against WN 7 Medium's 17.3 kB, WN 6 Long 9.2 kB against WN 6 Medium's 11.5 kB.

**Causes, with what each cost.**

1. **The start-up climb (most of the gap at high SNR).** The walk starts at `drc.default_speed` (WN 2 Long) and goes up one rung per `clean_run` (2) clean bursts, and the receiver can only pass each step on when it wins the channel (at AWGN 30 dB seed 1 the sender made 5 WN 3 Long transmissions in a row before the receiver got one in, about 150 s). With 9 rungs it reached WN 8 Long only at 980 s of 1800 at AWGN 30 dB, and around 700 to 1300 s on Poor. The same table started at WN 8 Long (`adaptive:...@wn8-long`, AWGN 30 dB) delivers 2175, exactly the fixed baseline, against 1660: the whole AWGN gap is the climb. After 1000 s the adaptive runs carry about 92 to 100% of the fixed WN 8 Long rate (new data counted from the seed 1 timelines).
2. **Latency-only rungs in the bulk walk.** The ladder is the union of the bulk and latency sets, so the MAX-SPEED walk (trial and probe, one rung each) steps through WN 6 Long and WN 7 Long, which are only on the ladder for latency, carry 20% less per transmission than the Medium rung of the same rate, and add two steps to every climb: 14 to 32% of the bulk sender's air time at 12 to 30 dB in round 1. A table with them dropped (ladder WN 1, 2, 3, 13 Long, WN 6 and 7 Medium, WN 8 Long, thresholds cut to match): AWGN 18 dB 1855, Poor 18 dB 1538, Poor 30 dB 1814 (+12%, +22%, +20% over the fix below), but AWGN 12 dB 1108 (-12%) and Poor 12 dB 763 (-4%). Changing the ladder rule is open question 15, so not done here.
3. **The answered rung dropped by short transmissions (a defect, interp C-1281).** When the flow window left a few kB after a WN 7 Long transmission, the bulk sender's next transmission held under one block and went at the LOW-SPEED rung (WN 13 Long or WN 6 Long); its answer made that the answered rung, and the next bulk transmission could go only one rung above it, so the sender re-climbed WN 6 Medium, WN 6 Long, WN 7 Medium, WN 7 Long and never reached WN 8 Long at Poor 18 dB seed 1. Fixed (corpus 0.35.0): the answered rung is the highest answered since the connection or the last silent wait. Poor 18 dB 1152 to 1259 (+9%); Poor 12 and 30 dB and every AWGN point within noise (AWGN unchanged to the bit: no short transmissions there).
4. **Errors diluted by fill (a defect, interp C-1270).** The receiver's burst error rate counted every valid header, and a WN 8 Long burst holds 24 data D_PDUs and about 100 ACK-ONLY copies as fill, so losing every segment read under 200 per thousand and never capped the rung (`rx_error_limit_ppt` 500 is a data D_PDU rate, as calibrated). At AWGN 12 dB, WN 8 Long had 86% of its data retransmitted with no cap; at AWGN 9 dB the walk sat on WN 7 Medium with 67% retransmitted. Fixed (corpus 0.35.0): only D_PDUs whose data part a CRC-32 checks count. AWGN 9 dB 471 to 857 (+82%), AWGN 12 dB 1255 to 1284; Poor 12 dB 794 to 719 (seed 1 diverged; the rung counts are the same), and AWGN 18 dB, Poor 18 and 30 dB and Poor-SSB 18 dB identical to the bit.
5. **Any error drops MAX-SPEED to the estimate's target on fading.** With the probe, one corrupted D_PDU in a WN 8 Long burst (40% of WN 8 Long bursts at Poor 18 dB have one or more, though only 2% of D_PDUs) lets the saturated Poor estimate move MAX-SPEED down to WN 6 Long, and the probe needs three steps back. The P7-9d trial (move down on the estimate only over the error limit) was re-measured on top of the fix: AWGN 12 dB 530, Poor 12 dB 621, Poor 18 dB 1389 (2 seeds): it holds WN 8 Long where it loses most of its data, so not kept.
6. **The bulk objective at mid SNR.** The table's bulk target is the rung of highest rate x (1 - D_PDU error rate), which ignores what ARQ costs: at AWGN 9 dB it keeps WN 7 Medium at about 30% D_PDU errors, where WN 6 Long delivers twice as much. A table built with rate x (1 - error rate) squared: AWGN 9 dB 775 against 471 (3 seeds, before the dilution fix, which already gives 857). The objective is design shape item 5, so not changed here.

**Tuning.** `clean_run` and `probe_run` from 2 to 1 (s5066-conformance calibration-results.md): on the fixed core, AWGN 18 dB 1758, Poor 18 dB 1466, Poor 30 dB 1783, Poor-SSB 18 dB 998 (+6%, +16%, +18%, +16%), AWGN 12 dB 1142 (-9%) and Poor 12 dB 769 (-3%), no links lost. `clean_run` 1 with `probe_run` 2 was in between and not taken.

**Before and after** (seeds 1 to 3, bit/s; round 1 is main 719123f with the round 1 table; after is this branch, both fixes and `clean_run` 1, `probe_run` 1; the gate is fixed WN 8 Long on AWGN and Poor, WN 7 Long on Poor-SSB):

| Point | Round 1 | After | Gate baseline | Best fixed rung |
|---|---|---|---|---|
| AWGN 9 dB | 471 | 807 | 0 | 1055 (WN 6 Long) |
| AWGN 12 dB | 1256 | 1208 | 0 | 1466 (WN 7 Long) |
| AWGN 18 dB (30 dB the same) | 1660 | 1758 | 2175 | 2175 |
| Poor 12 dB | 797 | 769 | 0 | 1083 (WN 6 Long) |
| Poor 18 dB | 1152 | 1466 | 1924 | 1924 |
| Poor 30 dB | 1557 | 1783 | 2059 | 2059 |
| Poor-SSB 18 dB | 860 | 1008 | 1133 | 1133 |

No links were lost in any of these runs. What is left at AWGN 15 dB and above and Poor 18 dB and above is mostly the climb (cause 1) and the latency-only rungs (cause 2), which are rulings, not defects.

## P7-9 round 3 (2026-11-09)

The six coordinator rulings of 2026-11-09 (corpus 0.36.0): the receiver's estimate walk goes straight to its target and the sender straight to the recommendation (no one-rung climb limit; after a failed trial it stays at or below the answered rung until the peer answers); MAX-SPEED walks only the bulk set's rungs and LOW-SPEED only the latency set's (`bulk_rungs`, `latency_rungs`); a burst with a lost block is not clean; the bulk objective is rate x (1 - e)^2; the latency choice is the least time on air for a 128-byte message, so the block duration counts and LOW-SPEED moves to Medium, Short and Ultra Short rungs; and `merge` writes `gate.tsv`. Studybox, 1800 s, seeds 1 to 3, the rebuilt calibrated table, `sweep-node.json` as round 2. Round 2 is main 8d89b5f with its table (the same runs as the round 2 table above).

| Point | Round 2 | Round 3 | Per seed | Gate baseline | Gate | Best fixed rung |
|---|---|---|---|---|---|---|
| AWGN 9 dB | 807 | 1089 | 1224, 960, 1083 | 0 | pass | 1055 (WN 6 Long) |
| AWGN 12 dB | 1208 | 1921 | 2024, 1883, 1855 | 0 | pass | 1466 (WN 7 Long) |
| AWGN 18 dB | 1758 | 2408 | 2505, 2486, 2232 | 2175 | pass | 2175 |
| AWGN 30 dB | 1758 | 2408 | 2505, 2486, 2232 | 2175 | pass | 2175 |
| Poor 12 dB | 769 | 976 | 951, 951, 1026 | 0 | pass | 1083 (WN 6 Long) |
| Poor 18 dB | 1466 | 1660 | 1704, 1808, 1469 | 1924 | fail (-14%) | 1924 |
| Poor 30 dB | 1783 | 2407 | 2505, 2382, 2335 | 2059 | pass | 2059 |
| Poor-SSB 18 dB | 1008 | 983 | 1008, 810, 1130 | 1133 (WN 7 Long) | goodput fail (-13%); SSB gate pass | 1133 |

No link was lost in any run. On Poor-SSB the gate of open question 14 is not goodput but no link lost and under 5% of transmissions on rungs that never decode there: it passes (no link lost, and no transmission on WN 8 or any other rung that never decoded; review of 2026-11-09), while `gate.tsv` applies the goodput rule to every channel and so reads fail there (983 against 1133). The gate column applies the plan 12 rule to these points alone (the WN 8 Long ceilings from round 1 are 2175 on AWGN and 2184 on Poor, so Poor 30 dB at 2059 is not at its ceiling and needs strictly above); the full re-sweep decides it.

- **Why it is faster.** The climb from WN 2 Long now takes four exchanges (WN 2 Long, WN 13 Long, WN 6 Medium, WN 7 Medium, WN 8 Long; each rung's estimate backs only the next few on the table), so AWGN 30 dB reaches WN 8 Long at about 250 s, against 980 s in round 1. And the acknowledging node's turns are short: its transmissions averaged 2.1 to 6.1 s on air (round 2: 7.5 to 8.8 s, a Long block each), its ACK-only bursts 1.1 to 2.7 s. That shortens every cycle, which is why adaptive now beats fixed WN 8 Long, whose acknowledgements still ride a 7.68 s block.
- **Poor 18 dB, what is left.** The bulk sender spends 29 to 44% of its air time on WN 13 Long and WN 6 Medium after a partial or collided WN 8 Long burst: the cap and the errored burst's estimate (saturated near 4 to 5 dB on fading) drop MAX-SPEED to the estimate's target, and the probe climbs back one bulk rung per clean burst. That is cause 5 of round 2, not addressed by the round 3 rulings.
- **Issue #67** (one-block ACK-only bursts all or nothing). Over the 24 runs the acknowledging node sent 135 ACK-only transmissions, 2 were lost (collided or missed, 1.5%), and no link was lost; on Poor 12 dB 0 of 10. With short-block acknowledgements a lost one costs about one answer wait at worst, and the silence rule never came near 16. Evidence for closing: the risk is acceptable at these loss rates.

## P7-9 round 4 (2026-11-09)

The coordinator rulings (a) to (d) of 2026-11-09 and the further ruling the same day (corpus 0.37.0): (a) with the probe, MAX-SPEED moves down only after a burst whose D_PDU error rate is over `rx_error_limit_ppt` or which has a lost block, and then one position of the bulk walk at most, the caps still applying; (b) a move up of LOW-SPEED may be backed by a clean run on a higher received rung; (c) issue #38, the reception window allows each later transmission one block and 1.5 s of host latency (non-ARQ only, so no effect on these runs); (d) `gate.tsv` gates SSB channels on no link lost and under 5% of transmissions on rungs that never decode. Studybox, 1800 s, seeds 1 to 3, the same table and `sweep-node.json` as round 3. The gate column is `gate.tsv` from a merge of these runs with the round 1 fixed runs (all 891, so the baseline rungs and ceilings are the full sweep's). "First form" is (a) as first ruled, where any burst that was not clean moved MAX-SPEED down one bulk rung.

| Point | Round 3 | Round 4, first form | Round 4 | Per seed | Gate baseline | Gate |
|---|---|---|---|---|---|---|
| AWGN 9 dB | 1089 | 1161 | 1161 | 1328, 1045, 1111 | 0 | pass |
| AWGN 12 dB | 1921 | 1874 | 1874 | 2015, 1798, 1808 | 0 | pass |
| AWGN 18 dB | 2408 | 2367 | 2367 | 2505, 2486, 2109 | 2175 | pass |
| AWGN 30 dB | 2408 | 2367 | 2367 | 2505, 2486, 2109 | 2175 | pass |
| Poor 12 dB | 976 | 1039 | 1001 | 1017, 1121, 866 | 0 | pass |
| Poor 18 dB | 1660 | 1698 | 1999 | 2156, 2194, 1648 | 1924 | pass |
| Poor 30 dB | 2407 | 2185 | 2185 | 2335, 2260, 1959 | 2059 | pass |
| Poor-SSB 18 dB | 983 | 998 | 1168 | 1234, 1064, 1205 | 1133 (WN 7 Long) | pass (SSB rule: no link lost, 0% on rungs that never decode; goodput also above) |

No link was lost in any run. All eight points pass. AWGN and Poor 30 dB are identical to the bit between the two forms (no burst there had errors under the limit). Apart from Poor 18 dB and Poor-SSB 18 dB, the changes from round 3 are within the 10 to 20% a single seed moves when contention order diverges, except that the slow start of LOW-SPEED is gone: the bulk sender spent 0 to 87 s on WN 1 Medium early in each round 3 run on Poor and Poor-SSB, and 0 s in every round 4 run.

- **Poor 18 dB, why the first form was not enough.** It cut the bulk sender's air time on WN 13 Long and WN 6 Medium from 29 to 45% to 14 to 28%, but the step landed on WN 7 Medium: 28 to 35% of its air time (11 to 18% in round 3), where WN 7 Medium lost 22% of its D_PDUs (270 of 1247), against 0.4% in round 3, where it was only reached by climbing in good spells. The step down follows a fade, and the next burst meets a new one: at 18 dB on Poor the fades are short against a 25 s burst and hit WN 7 Medium as they hit WN 8 Long. Fixed WN 8 Long at the same point delivers 99% of its D_PDUs (round 1, 13 of 32 bursts partial, a few D_PDUs each). Holding the rung on a burst with a few corrupted D_PDUs and no lost block took Poor 18 dB from 1698 to 1999 and Poor-SSB 18 dB from 998 to 1168; Poor 12 dB 1039 to 1001 is within noise. It differs from the P7-9d trial, which round 2 rejected at AWGN 12 dB and Poor 12 dB, in two ways: a lost block (a collision, or a fade that took whole blocks) still moves MAX-SPEED down, and the move is one bulk rung.

## P7-9e: only an acknowledgement answers data (2026-11-10)

Coordinator ruling (g) of 2026-11-10 (corpus 0.38.0): for the sender's rate logic a transmission carrying ARQ data counts as answered only when the peer acknowledges at least one of its D_PDUs; if its `dts.ack_wait` expires first it is a silent wait, and a failed trial when it was a trial, whatever else the peer sent meanwhile (interp C-1281, C-1282). Link failure (C-1250) still counts any answer. The defect it fixes was found by the P7-10 preparation: on Poor-SSB 30 dB seed 1 node a sent 17 bulk transmissions on WN 8 Long, all lost, because node b's own chat counted as an answer, and node b, decoding no header from them, kept recommending WN 8 Long. The ruling's second option, a receiver that treats its recommendation as stale after bursts with no valid header from the peer, was not needed and is not implemented.

Studybox, 1800 s, seeds 1 to 3, the same table and `sweep-node.json` as round 4. Before is round 4 (its runs; Poor-SSB 30 dB run now on the round 4 build); after is P7-9e. Goodput in bit/s, mean then per seed.

| Point | Before | Per seed | After | Per seed |
|---|---|---|---|---|
| AWGN 9 dB | 1161 | 1328, 1045, 1111 | 1130 | 1328, 1045, 1017 |
| AWGN 12 dB | 1874 | 2015, 1798, 1808 | 1921 | 2015, 1798, 1949 |
| AWGN 18 dB | 2367 | 2505, 2486, 2109 | 2442 | 2505, 2486, 2335 |
| AWGN 30 dB | 2367 | 2505, 2486, 2109 | 2442 | 2505, 2486, 2335 |
| Poor 12 dB | 1001 | 1017, 1121, 866 | 1077 | 1017, 1121, 1092 |
| Poor 18 dB | 1999 | 2156, 2194, 1648 | 2125 | 2156, 2194, 2024 |
| Poor 30 dB | 2185 | 2335, 2260, 1959 | 2241 | 2335, 2260, 2128 |
| Poor-SSB 18 dB | 1168 | 1234, 1064, 1205 | 1168 | 1234, 1064, 1205 |
| Poor-SSB 30 dB | 565 | 151, 480, 1064 | 1026 | 1158, 1055, 866 |

No link was lost in any run. Seeds 1 and 2 are identical to the bit at every point but Poor-SSB 30 dB: the change altered no decision in them. Seed 3 diverges at every point but Poor-SSB 18 dB, after one such wait changes the contention order; the changes are within the 10 to 20% a seed moves when that happens (AWGN 9 dB seed 3 1111 to 1017 is the only fall). On Poor-SSB 30 dB the transmissions on WN 8 Long, the rung that never decodes there, went from 17, 10 and 1 per run to 4, 3 and 3 (10 of 207 transmissions in all, 4.8%, before ruling (f) leaves out those before each node's first ceiling): each is now a failed trial, and the failed-trial ceiling, doubling to `tx_ceiling_cap`, spaces them out; node b still recommends WN 8 Long throughout, since it never decodes a header of them.

Review change (same corpus): an ACK that acknowledges none of the awaited transmission's D_PDUs and marks them all failed (C-0908, C-0910) stops `dts.ack_wait` without an expiry. As first built that left the transmission neither answered nor failed, and its retransmission at the same rung was a trial again, so a peer that sends its receive state with its own traffic, whether or not it has new data to acknowledge (C-1128 leaves that to the implementation), would have reproduced the defect. Such an ACK now counts as the silent wait it replaces (interp C-1281, scenario `drc/tx-ack-failing-all-is-no-answer`). Between two of our nodes it arises only when a node acknowledges data it decoded from that transmission without covering any of its D_PDUs, which our acknowledgement rules do not do. Checked on studybox: Poor and Poor-SSB at 18 and 30 dB, seeds 1 and 3, 1800 s, eight runs before and after the change, every result file identical but for build versions and timings, so the table above stands.
