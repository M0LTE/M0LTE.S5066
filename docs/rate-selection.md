# Rate selection: throughput against SNR (P7-10)

> Skeleton. Every `TO FILL` comes from the final sweep (`s5066-sweep --profile final`, docs/stack-sweep.md); nothing here is a measurement yet.

This page publishes what the node's adaptive rate selection (phase 7) delivers over simulated HF channels, against the best single fixed waveform an operator could pick without knowing the channel. It is the evidence for the phase 7 done-when of plan 12.

**In short.** TO FILL: one paragraph. Where adaptive beats the baseline, by how much at low, mid and high SNR on each channel, how far it sits below the best fixed rung at each point, and whether any link was lost.

## Method

**What runs.** Two whole nodes (core, host input order, an in-process M0LTE.Ms110d modem each) joined by one continuous Watterson channel per direction (M0LTE.HfChannel), on a virtual clock. Node a sends 2048-byte ARQ U_PDUs to node b as fast as flow control allows; node b sends a 100-byte ARQ message to a every 60 s. Each run is 30 minutes of channel; goodput is the bulk bytes delivered after the first 60 s, in bit/s. Details: docs/stack-sweep.md.

**Channels and SNR.** AWGN, Poor (ITU-R F.1487 Poor, Watterson), and Poor with the SSB band-pass of both radios (400 to 2600 Hz). The set SNR is the signal power at the channel input, after the transmit passband where there is one, over the noise in 3000 Hz at the receiver's antenna (the P7-5 reference). 0 to 30 dB in 3 dB steps; seeds 1 to 5 per point; every channel realisation, payload and contention draw follows from the seed.

**The two arms.**

- Adaptive: both nodes load the calibrated `drc.table` the host ships (below). The receiver recommends a bulk rung (MAX-SPEED) and a latency rung (LOW-SPEED) from its smoothed SNR estimate, clean-burst runs and error caps; the sender uses them, with a trial and a failed-trial ceiling when it climbs (core-architecture.md section 9, design shape items 1 to 7, interp C-1270, C-1281, C-1291).
- Fixed: an empty `drc.table` and `drc.default_speed` set to one rung, WN 1 to 8 or 13 at the Long interleaver, for the whole run (open question 13). Nine rungs.

Both arms run the same node settings (`tools/s5066-sweep/sweep-node.json`: 8 contention slots of 3 s, listen-before-talk 2 s after DCD and 3 s after VDCD, transmissions of at most 30 s, so a 57 s answer wait) and the same first-data wait, `cas.first_data_timeout_ms` 456000 ms, the value the calibrated table derives (8 answer waits; interp B-0420), set on both so neither arm is favoured.

**The baseline (open question 14).** Per channel, the fixed rung with the highest goodput summed over the sweep. Each point's best fixed rung (an oracle that knows the channel) is shown beside it with the gap, not gated.

**The gate (plan 12, phase 7 done-when).** On AWGN and Poor, at every SNR where any configuration delivers data: adaptive's mean over the seeds strictly above the baseline's mean, or, where the baseline rung is at its measured stack ceiling (its mean within 2% of its highest on the channel), within 2% of that ceiling. On Poor with the SSB band-pass: no link lost, and under 5% of the transmissions after each node's first failed-trial ceiling on rungs that never decode on that channel (rulings (d) and (f) of 2026-11-09). The merge writes the verdict per point (`gate.tsv`).

## Versions

| What | Version |
|---|---|
| M0LTE.S5066 commit | TO FILL (`merge.json` versions, `s5066_core`) |
| Corpus (s5066-conformance) | TO FILL (0.37.0 at the time of writing) |
| Calibrated table | s5066-conformance `calibration/ms110d-cs-0.6.1/drc-table.json`, built by `tools/build_drc_table.py` (bulk objective rate x (1 - e)^2, latency target 100 per mille for a 128-byte message); FNV-1a 64 TO FILL (`plan.json`) |
| Modem | M0LTE.Ms110d TO FILL (`ms110d` in `merge.json`; the table was calibrated on 0.6.1, commit be49721) |
| Channel model | M0LTE.HfChannel TO FILL (`hfchannel`), SSB passband TO FILL (`ssb_passband`) |
| Sweep | `s5066-sweep --profile final`, TO FILL runs of 1650 merged, TO FILL date, TO FILL instance |

## Results

One plot per channel: adaptive's mean over the seeds with the range from its lowest to its highest seed, the baseline rung the same way, the best fixed rung at each point (dashed) and the other fixed rungs (grey). Made by `tools/s5066-sweep/plot_curves.py` from the merged aggregates.

### AWGN

![AWGN: goodput against set SNR](rate-selection/curve-awgn.svg)

TO FILL: baseline rung, a few sentences, and the table below from `points.tsv`.

| Set SNR (dB) | Adaptive mean (min to max) | Baseline mean (min to max) | Best fixed rung | Gain over baseline | Gap to best fixed | Links lost | Gate |
|---|---|---|---|---|---|---|---|
| 0 to 30 | TO FILL | | | | | | |

### Poor

![Poor: goodput against set SNR](rate-selection/curve-poor.svg)

TO FILL, as for AWGN.

### Poor with SSB band-pass

![Poor with SSB band-pass: goodput against set SNR](rate-selection/curve-poor-ssb.svg)

TO FILL, as for AWGN, plus the SSB gate inputs per point (links lost, transmissions after the first ceiling, the share on rungs that never decode) and which rungs never decode on this channel.

## Done-when, point by point

TO FILL from `gate.tsv`: every gated point with its verdict, the points not gated (nothing delivers) named, and the SSB rule's result. Phase 7 is done when every AWGN and Poor point passes and the SSB rule holds.

## Data

Everything behind this page is in `docs/rate-selection/`:

- `points.tsv`: every point of the published curve (channel, set SNR, adaptive and baseline mean, minimum and maximum, best fixed rung, gains, links lost, gate rule and verdict).
- `gate.tsv`, `baseline.tsv`, `curve-<channel>.tsv` (every configuration at every point, with retransmissions, chat latency and transmissions per rung), `turnaround.tsv`, `overshoot.tsv`.
- `merge.json` (runs planned and merged, build versions) and `plan.json` (the grid, the table's hash and the node settings).
- `curve-<channel>.svg` (and `.png` where matplotlib was available).

The per-run JSON files are archived with the research notes (TO FILL: path), not in the repository.

## Reproducing

docs/stack-sweep.md, "The final sweep (P7-10)": the spot-box commands, then `merge --profile final` and `plot_curves.py`. Any single point can be re-run on a workstation with `s5066-sweep run --channel <channel> --snr <dB> --config <config> --seed <n> --duration-s 1800 --first-data-ms 456000`.
