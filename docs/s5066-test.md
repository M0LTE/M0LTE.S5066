# s5066-test

`s5066-test` is the test client for a STANAG 5066 node (plan 11.6). It connects to a node's SIS on TCP like any other application, so it works with `s5066d` and with any other node that speaks Annex S. Its SIS part does three things:

- **chat**: HF Operator Chat lines (Annex O, SAP 5);
- **file**: a file sent as ARQ U_PDUs with delivery confirmation, checked at the far end by SHA-256;
- **perf**: N messages of S bytes, timing each confirmation and, optionally, each round trip.

Every activity needs the tool running at both ends: the far end receives files, checks them and echoes performance messages. Either the interactive mode or `serve` does that.

It also has a receive-only monitor (plan 11.6 part 2): see [The monitor](#the-monitor).

## Running it

Build it from the repository with `dotnet build tools/s5066-test`, or run it with `dotnet run --project tools/s5066-test -- <arguments>`.

To try it on one machine, start the two bench nodes and point one `s5066-test` at each:

```
s5066d src/s5066d/bench-node1.json      # node 0.0.1.1, SIS on 127.0.0.1:5066
s5066d src/s5066d/bench-node2.json      # node 0.0.1.2, SIS on 127.0.0.1:5067
s5066-test --node 127.0.0.1:5067        # the far end, interactive
s5066-test --node 127.0.0.1:5066        # and this end
```

Add `--trace` before a node's configuration file to log every D_PDU, timer and burst it handles, for when a transfer goes wrong.

## Interactive mode

A plain line-mode screen that works over ssh. Type a line to send it as chat. Commands:

| Command | What it does |
|---|---|
| `/to 0.0.1.2` | send to one node; `16.x.y.z` to `31.x.y.z` is a group, `/all` is broadcast |
| `/file path [none\|node\|client]` | send a file; client confirmation by default |
| `/perf 10 200 [nonarq] [none\|node\|client] [echo] [window 8]` | 10 messages of 200 bytes |
| `/info` | the node's speed, interleaver and SNR per peer |
| `/help`, `/quit` | |

It starts addressed to all (broadcast), so chat goes non-ARQ until you pick a node with `/to`. Received chat, files and problems show as they happen.

## Headless mode

For scripts and tests. Each command prints a JSON result and exits 0 on pass, 1 on fail (2 for a usage error). Progress goes to stderr.

```
s5066-test chat --node 127.0.0.1:5066 --to 0.0.1.2 --text "hello"
s5066-test file --node 127.0.0.1:5066 --to 0.0.1.2 --path report.pdf --confirm client
s5066-test perf --node 127.0.0.1:5066 --to 0.0.1.2 --count 20 --size 500 --echo
s5066-test info --node 127.0.0.1:5066
s5066-test serve --node 127.0.0.1:5067 --save-dir received
```

Other options: `--sap` (the data SAP, default 13), `--priority`, `--ttl` (in 2 s units, 0 for the node's default), `--window` (U_PDUs waiting for an answer at once, default 8), `--mode arq|nonarq`, `--repeats` (non-ARQ), `--segment-bytes` (data bytes per file U_PDU, default the MTU less 9), `--no-verdict` (do not wait for the far end's hash), `--timeout` seconds, `--json file` (write the result there instead of stdout).

### Scenarios

`s5066-test run scenario.json` drives several nodes in one run. It connects to every node first, so each one answers for the far end of the others, then runs the steps in order and stops at the first failure.

```json
{
  "nodes": { "a": "127.0.0.1:5066", "b": "127.0.0.1:5067" },
  "steps": [
    { "do": "chat", "node": "a", "to": "0.0.1.2", "text": "hello" },
    { "do": "expect_chat", "node": "b", "from": "0.0.1.1", "text": "hello" },
    { "do": "file", "node": "a", "to": "0.0.1.2", "bytes": 5000, "confirm": "client" },
    { "do": "expect_file", "node": "b", "from": "0.0.1.1" },
    { "do": "perf", "node": "b", "to": "0.0.1.1", "count": 10, "size": 200, "echo": true },
    { "do": "info", "node": "a" }
  ]
}
```

Steps: `chat`, `expect_chat`, `file` (`path` relative to the scenario, or `bytes` of random data), `expect_file`, `perf`, `info` and `sleep` (`seconds`). Each takes the same options as the commands above, in snake_case, plus `timeout_s` (120 s by default, 900 s for files and perf). Top level: `sap`, `save_dir` and `continue_on_failure`.

The result lists every step with `pass`, `elapsed_ms`, an `error` if it failed, and a `detail`: the confirmation status for chat; for a file the U_PDUs confirmed and rejected (with reasons), the goodput and whether the far end's hash matches; for perf the counts, minimum, mean, median and maximum confirmation and round-trip times, and the goodput.

## What passes

- chat: confirmed (to one node) or sent (to a group);
- file: every U_PDU confirmed, none rejected, and the far end's SHA-256 matches;
- perf: every message sent, every one confirmed when confirmation was asked for, every one echoed with `echo`;
- expect steps: the line or file arrived before the timeout, and a file's hash matched.

Without confirmation the sender cannot see delivery. A file with `confirm` `none` still waits for the far end's hash, so it proves delivery; with `--no-verdict` as well it passes once the node has taken every U_PDU. Perf with `none` and no `echo` passes once the node has taken every message, and its goodput is then only the rate the node accepted them. Use `echo` or a confirmation to measure the link.

## On the air

These are the tool's own choices, not part of STANAG 5066:

- Files and perf use SAP 13 by default, which Annex F leaves unallocated [Ed4 F Table F-1, p.F-1]; a deployment prefers unallocated SAPs for its own applications [Ed4 F.2, p.F-2]. Change it with `--sap` (the same at both ends).
- Each U_PDU starts with a kind byte, a random transfer ID and a sequence number, so the client library can match each confirmation to its send. Files are a header (size, segment count, SHA-256, name), then segments that fill the MTU; the far end answers with its own SHA-256. Perf messages are padded to the size asked for; an echo is the first 10 bytes only, so a round trip is S bytes out and 10 back.
- Times are measured from when a U_PDU is handed to the client library, so they include any wait while the node has flow off.
- File transfer uses plain ARQ unidata. It will move to CFTP (Annex V) once that exists.

## The monitor

`s5066-test monitor` listens to a channel and prints every D_PDU it hears. It runs its own MS110D receiver and decoder, so it is an independent view of what went over the air, not what a node thinks happened. It never transmits: it opens no playback device and cannot key a radio.

Pick one audio source:

```
s5066-test monitor --wav recording.wav                       # a recording, then exit
s5066-test monitor --udp 127.0.0.1:7302                      # UDP audio, as s5066d sends it
s5066-test monitor --card plughw:CARD=Device,DEV=0 [--rate 48000]   # a sound card
```

- **WAV**: 16, 24 or 32-bit PCM or 32-bit float, mono or the first channel, at 9600 Hz or a whole multiple of it (48000 Hz is fine). Times are from the start of the file.
- **UDP**: the stream s5066d's `audio_peer` sends. Point a node's `audio_peer` at the monitor, or feed it from an audio bridge. It only hears what is sent to that port.
- **Sound card**: any ALSA capture device, for example a receiver such as an UberSDR on its own audio interface. Times are UTC.

Add `--jsonl file` to write the same records as JSON lines for later analysis, and `--quiet` to print nothing but errors. Ctrl-C stops a live run. Each run ends with a summary per source and destination.

### Reading the output

```
+00:00:00.505 BURST 1 start WN6/S K7 snr 22.6dB cfo +0.0Hz
+00:00:01.718 WN6/S 31.8dB 0.0.1.1 > 0.0.1.2 DATA-ONLY fsn=0 flags=SEI eow=used:WN6/S eot=6(3.0s) len=100 crc=ok
+00:00:01.718 BURST 1 end eom 1.21s 1 blocks snr 31.8dB: 2 D_PDUs, 1 with data (0 CRC bad), 0 cut, 0 bad headers
LINK 0.0.1.1 > 0.0.1.2: 1 bursts, 2 D_PDUs, 1 data (0 CRC bad, 100 bytes good), 0 acks, 0 cut; last WN6/S snr 31.8dB
```

A D_PDU line gives the time, the burst's waveform and interleaver, the receiver's SNR for the block it arrived in, source and destination (`(grp)` for a group), the type, its sequence numbers and windows (`fsn`, `lwe`, `cpdu-id`, `offset`, `rx-window` and so on), the EOW, the EOT in half seconds, the data length and the CRC result:

- `crc=ok`: the data CRC passed; `crc=hdr-ok` for a type with no data CRC.
- `crc=BAD`: the header was good but the data was damaged.
- `crc=CUT`: the burst ended inside it.

Flags are letters: `S` C_PDU start, `E` end, `I` deliver in order, `D` drop, `U` and `L` the transmit window edges. A burst end line counts what the burst held; "bad headers" are syncs whose header failed its check, usually noise or a D_PDU lost to errors.

The monitor knows nothing about the nodes, so it reads every D_PDU as Edition 4 (types 0 to 8 and 15 read the same in Edition 3). It does not reassemble C_PDUs.
