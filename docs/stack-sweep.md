# Stack sweep runbook (P7-8)

`tools/s5066-sweep` runs two whole nodes against each other over a simulated HF channel and measures what the stack delivers: goodput, retransmissions, transmissions per rung, links lost, the gap between bursts and the EOT overshoot (issue #66). It is the T4 harness of core-architecture.md section 9 (P7-8), used for the fixed-rate baselines, the P7-9 tuning rounds and the P7-10 curve. It is a statistical tool, not byte-exact work.

## What one run is

- Two `Node`s (0.0.1.1 and 0.0.1.2), each with an in-process MS110D modem (M0LTE.Ms110d), joined by one continuous Watterson channel per direction (M0LTE.HfChannel: AWGN, Poor or Moderate, optionally the SSB band-pass on both radios), on a virtual clock in 20 ms steps. Inputs reach each core in the host's order (core-architecture.md 12.3, 12.4), and modem events follow the rules of 12.5 and 12.6. A run goes as fast as the two demodulators and repeats exactly for a seed.
- Traffic: node a sends bulk ARQ U_PDUs of 2048 bytes to node b as fast as flow control allows; node b sends a 100-byte ARQ chat message to a every 60 s from 30 s.
- Both nodes take the corpus `defaults.json`, then `tools/s5066-sweep/sweep-node.json` (CSMA and transmission length as the two-node bench, with the reasons in its comments), then the run's `drc.default_speed` and `drc.table`.
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
- `merge --profile baseline --out DIR`: writes `DIR/aggregate/`: `curve-<channel>.tsv` (per set SNR and configuration over the seeds: goodput mean, minimum and maximum, retransmission %, links lost, chat delivered and latency, transmissions per rung), `baseline.tsv` (per channel the fixed rung with the highest goodput summed over the sweep, open question 14, and each point's best fixed rung), `turnaround.tsv`, `overshoot.tsv`, `merge.json` (missing and failed runs) and a copy of `plan.json`.
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

Measured on studybox (8 cores, 2026-11-08), Release, both nodes' demodulators counted: 0.15 to 0.26 core-seconds per channel-second for single runs on a quiet box, 0.35 to 0.48 with four workers while another sweep shared the box. Fading and the SSB band-pass cost a little more than AWGN. A run uses well under 100 MB.

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
