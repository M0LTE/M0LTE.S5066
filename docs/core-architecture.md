# Core architecture: the sans-IO runtime of M0LTE.S5066.Core

Design for issue #18 (P3-0). Version 1.0, 2026-11-07. This is the runtime every sublayer is built on in phases 3 to 6, by other agents. It fixes the shapes that must agree between the C# core and the later C++ port: the input and output types, the time and timer model, how the sublayers compose, how a transmission is planned, and the determinism rules. It does not restate protocol behaviour; that comes from the matrix rows each work item cites.

Read with: `docs/plan.md` 3.2, 5.1, 5.4, 7 and 12; the conformance repository's `docs/corpus-format.md` (sections 4 to 6), `docs/harness-contract.md` and `docs/prng.md`; and the phase 1 codecs in `src/M0LTE.S5066.Core`.

Conventions in this document: type sketches are C# and are indicative, not final signatures. Matrix rows are cited as `C-0843`. Anything the standard does not say is marked NOT STATED and listed in section 10 with the row that should record our choice.

## Contents

1. Shape of the core
2. Inputs and outputs
3. Time and timers
4. Composition: SIS, CAS, DTS, MAC
5. The transmission planner
6. Determinism rules for a byte-identical C++ port
7. Configuration
8. The C# T2 scenario runner
9. Work breakdown, phases 3 to 6
10. Choices the standard does not make (NOT STATED)
11. Proposed changes to the corpus format and harness contract

---

## 1. Shape of the core

One object, `Node`, is the whole protocol stack of one STANAG 5066 node. It is created from a configuration, a PRNG seed and a modem port, at time 0. It is driven by exactly the ten input kinds of corpus-format.md section 4.2 and produces exactly the eight output kinds of section 4.3. It owns no thread, socket, clock or random source. Everything it does is a pure function of the sequence of inputs it has been given, so a T2 trace is a complete description of its observable behaviour, and the C++ port reproduces the trace byte for byte.

```csharp
public sealed class Node
{
    public Node(NodeConfig config, ulong prngSeed, IModemPort modem);

    /// Handles one input at its time t, running to completion. Every output it causes is
    /// appended to the output list, in the order produced.
    public void Handle(CoreInput input);

    /// Returns and clears the outputs produced since the last call.
    public List<CoreOutput> DrainOutputs();
}
```

Inside, `Node` holds the four sublayers and the shared services they use:

```
                 CoreInput (t)                      CoreOutput
                      |                                 ^
  +-------------------v---------------------------------+------+
  | Node                                                        |
  |  Dispatcher: one input in, run to completion, FIFO of       |
  |  internal primitives, outputs appended in order             |
  |                                                             |
  |  Sis  <--C_ primitives-->  Cas  <--D_ primitives-->  Dts    |
  |                                                      |  ^   |
  |                               queue state, EOTs heard|  |   |
  |                                                      v  |   |
  |                                     Mac (Annex K) --> Planner|
  |                                                             |
  |  TimerTable   Outputs   SplitMix64   NodeConfig   PeerTable |
  +-------------------------------------------------------------+
                      |
                 IModemPort.PlanBurst (the only call out; pure)
```

The sublayer boundaries are the D_ and C_ primitives of Annexes B and C, as plan 3.2 principle 2 says. They are internal interfaces (C-0024, B-0009), so their C# spelling is ours, but their names and parameters follow Tables B-1 and C-1 so the code is traceable to the text (B-0018, C-0039 to C-0041).

What is deliberately not in the core: the Annex S TCP listener, PTT and audio, wall-clock TTD conversion for display, pcap writing, the status page and the station hooks. Those live in the host (plan D3) and `M0LTE.S5066.Station`.

---

## 2. Inputs and outputs

The types match the T2 trace format of corpus-format.md sections 4.2 and 4.3 exactly, key for key. The harness converts JSON to these records and back; the core never sees JSON (harness-contract.md section 3).

### 2.1 Inputs

```csharp
public enum Interleaver { UltraShort, VeryShort, Short, Medium, Long, VeryLong }   // the T2 enum, C-0469, plan 5.3
public enum BurstEndReason { Eom, SignalLost, Terminated }                           // T2 "reason"

public abstract record CoreInput(long T);                                            // T: ms since the core was created

// class 0
public sealed record ConfigInput(long T, NodeConfig NewConfig) : CoreInput(T);       // the merged result of "set"
// class 1
public sealed record TimerExpired(long T, TimerName Name) : CoreInput(T);
// class 2, modem
public sealed record BurstStarted(long T) : CoreInput(T);
public sealed record BytesReceived(long T, ReadOnlyMemory<byte> Bytes) : CoreInput(T);
public sealed record BurstEnded(long T, BurstEndReason Reason, int Wn, Interleaver Interleaver, int K,
                                int? SnrTenthDb, int Blocks) : CoreInput(T);
public sealed record TransmitDone(long T) : CoreInput(T);
public sealed record ChannelBusy(long T, bool Busy) : CoreInput(T);
// class 3, SIS
public sealed record SisConnect(long T, int Conn) : CoreInput(T);
public sealed record SisBytes(long T, int Conn, ReadOnlyMemory<byte> Bytes) : CoreInput(T);
public sealed record SisDisconnect(long T, int Conn) : CoreInput(T);
```

Notes:

- `ConfigInput` carries the whole new configuration, not the overlay. The harness does the merge (corpus-format.md section 6), so the core has one representation of configuration and no merge code to port.
- `SnrTenthDb` is the only signed quantity and is an integer in tenths of a dB, as the corpus requires (no floating point). The core stores it as given; the rate selector of phase 7 works in tenths of a dB too.
- `BurstEnded.Blocks` and the lock fields are recorded per peer for phase 7 and otherwise unused in phases 3 to 6.
- There is no START or STOP input (Annex K Table 1, K-0062; Figure K-1 STOP, K-0058). Construction is START: the node is in SENSE from time 0. STOP is the host destroying the core. See section 10.

### 2.2 Outputs

```csharp
public abstract record CoreOutput;

public sealed record Transmit(ReadOnlyMemory<byte> Bytes, int Wn, Interleaver Interleaver, int K, bool Eom) : CoreOutput;
public sealed record TerminateReceive : CoreOutput;
public sealed record SetMaxInputBlocks(int N) : CoreOutput;
public sealed record TimerStart(TimerName Name, long Ms) : CoreOutput;
public sealed record TimerCancel(TimerName Name) : CoreOutput;
public sealed record SisBytesOut(int Conn, ReadOnlyMemory<byte> Bytes) : CoreOutput;
public sealed record SisClose(int Conn) : CoreOutput;
public sealed record LinkState(uint Peer, LinkStateName State) : CoreOutput;
```

Notes:

- `Transmit` is an output, not a call on the modem port. The harness contract describes a fake modem whose `Transmit` "records a transmit output and returns the duration"; in this design the core does not consume that return value, because it has already planned the burst with `PlanBurst` (section 5) and one source of truth is better than two. Section 11 proposes wording that makes this explicit.
- `TerminateReceive` and `SetMaxInputBlocks` exist in the type system from day one but nothing in phases 3 to 6 emits them. We receive an unlimited number of blocks (110D D.5.4.6 c, value 0) and we never cut a reception short, since Annex K forbids transmitting while the channel is busy anyway. Phase 7 or 8 may use them.
- `TimerCancel` is emitted only for a timer the core believes is running (section 3.3). The format allows cancelling a stopped timer, but the trace is tighter and still deterministic if we do not.
- `LinkState` names are registered when the CAS lands (P4-1, P5-1). Proposed set: `unlinked`, `calling`, `linked`, `closing`, `breaking`.
- One `SisBytesOut` per encoded primitive. The core never concatenates two primitives into one output, so a trace reads one primitive per line and the C++ port has no buffering decisions to match.

### 2.3 Addresses

Addresses are `uint` 28-bit values throughout the core (`DpduAddress.MaxValue`), with the group flag carried separately where a PDU carries it (C-0122). The dotted form exists only in the harness and the host. A peer is identified by its unicast address; group addresses never key a per-peer table.

---

## 3. Time and timers

### 3.1 Time

- Time is `long` milliseconds since the core was created (time 0), corpus-format.md 4.4. The core's clock is the `T` of the input it is handling: `Node.Handle` stores it in `_now`, and every sublayer reads `_now` through the dispatcher. No sublayer stores a time it did not get from an input or compute from one.
- The core never converts time to or from wall-clock. The one place the standard needs wall-clock, the TTD field of a DATA S_PDU (A-0162, A-0163, A-0166, A-0167), is handled by the SIS as follows: the host supplies, in configuration, the UTC day-of-year and seconds-of-day that correspond to core time 0 (`time.epoch_day`, `time.epoch_second`, section 7). The core adds `_now` to that. In T2 the values come from `defaults.json`, so the TTD bytes in a trace are deterministic.
- Half seconds (EOT, reception window) are computed with integer arithmetic on milliseconds: `ceilHalfSeconds(ms) = (ms + 499) / 500`.

### 3.2 Timer names

```csharp
public enum TimerKind
{
    // csma
    CsmaLbtWait, CsmaContWait,
    // dts
    DtsAckWait /*peer*/, DtsManagementWait /*peer*/, DtsTtd, DtsReceptionWindow,
    // cas
    CasLinkRequest /*peer*/, CasFirstData /*peer*/, CasBreak /*peer*/, CasCleanClose /*peer*/,
    // sis
    SisConfirmWait, SisTtd, SisSoftLinkIdle /*peer*/, SisSoftLinkMinHold /*peer*/, SisSoftLinkMax /*peer*/,
}

/// A registered name: the kind plus its qualifier (a peer address or a conn), or none.
public readonly record struct TimerName(TimerKind Kind, uint? Peer = null, int? Conn = null)
{
    public string ToTraceString();                 // "dts.ack_wait/0.0.1.2", corpus-format.md 4.6
    public static TimerName Parse(string s);       // harness side only
}
```

The string forms are exactly the `<sublayer>.<timer>[/<qualifier>]` names of corpus-format.md 4.6. Each is registered in that document's table by the PR that first starts it (section 9 says which). The list above is the planned set for phases 3 to 6; a PR may add one, with its row, but should first try the collapsing rule below.

**Collapsing rule (one timer per deadline set).** Qualifiers may only be a peer address or a conn number, so a per-request timer cannot be named. Where a sublayer has many deadlines of one kind (U_PDUs awaiting confirmation, queued C_PDUs with TTDs, non-ARQ reassemblies with reception windows) it keeps a sorted list of deadlines and runs one named timer at the head. On expiry it processes every entry whose deadline is at or before `_now`, in list order, then re-arms for the new head. The list order is a total order (deadline, then insertion sequence number), so both cores process entries identically.

### 3.3 TimerTable

```csharp
internal sealed class TimerTable
{
    private readonly SortedDictionary<TimerName, long> _deadline;   // TimerName has a total order (section 6)

    /// Starts or restarts. Emits TimerStart(name, deadline - now). ms must be >= 1: a sublayer that wants
    /// "now" acts now instead (corpus-format.md 4.4).
    public void Start(TimerName name, long ms);

    /// Emits TimerCancel only if the timer is running.
    public void Cancel(TimerName name);

    public bool IsRunning(TimerName name);

    /// Called by the dispatcher for a TimerExpired input. The harness has already checked the deadline;
    /// the core also checks it and treats a mismatch as a bug (throws), never as a silent no-op.
    public void Expire(TimerName name, long now);
}
```

Restart is `Start` on a running timer: one `TimerStart` output, new deadline (corpus-format.md 4.4). There is no pause: the Annex K "hold while busy" reading of K-0087 is implemented as cancel on busy and start on idle, which is what a trace shows.

### 3.4 Delivery of expiries

The harness or host feeds `TimerExpired(name)` at the deadline. The dispatcher looks up `name.Kind` and calls the owning sublayer's `OnTimer(name)`. Nothing fires inside the core on its own; this is what harness-contract.md section 2 means by "the harness must not fire timers by itself", read from the core's side.

### 3.5 Same-millisecond ordering

Across inputs, the order is fixed by the trace or the host in the class order of corpus-format.md 4.5: configuration, timer expiries (in the order their `timer_start` outputs appeared), modem events, SIS events. The core does not reorder; it handles each input to completion before the next.

Within one input, the core's own order is fixed by three rules, and they are the whole of the intra-input ordering contract:

1. **Run to completion with a FIFO of primitives.** A sublayer never calls another sublayer directly. It enqueues a primitive on the dispatcher's FIFO (`Internal` queue) and returns. The dispatcher drains the FIFO in order until empty. So the ordering of cross-sublayer effects is the order of enqueueing, which is the order the code is written in. Outputs are appended to the output list the moment a sublayer produces them.
2. **Per-D_PDU processing order.** For a `BytesReceived` input, each D_PDU found in the bytes is processed completely, in byte order, before the next: (a) header-level effects (EOT to the MAC channel model, the heard-address record, SNR bookkeeping), (b) the per-peer DTS state machine, (c) any primitive it enqueues. Only then the next D_PDU.
3. **The MAC runs last.** After the FIFO is empty, the dispatcher calls `Mac.AfterInput()` exactly once. This is where the queue-empty or non-empty state is sampled and where a transition that depends on it is taken (SENSE to LBT_WAIT, LBT_WAIT to SENSE on an emptied queue). A transmission is built only in `AfterInput` or in the CONT_WAIT expiry handler, never in the middle of processing, so everything an input causes to be queued is eligible for the same transmission (C-0881, as late as possible).

The same three rules bind the C++ port. They are not optional style; a port that let the SIS call the CAS directly would produce outputs in a different order on the first scenario that exercises a round trip.

---

## 4. Composition: SIS, CAS, DTS, MAC

### 4.1 The internal interfaces

The primitives of Tables B-1 and C-1 become records on the dispatcher FIFO. Each carries the information the annexes list for it, no more (B-0017, C-0038: pass control information explicitly, never derived from U_PDU contents, B-0014, C-0036).

```csharp
// CAS to DTS (C-0039, C-0040, C-0041)
internal sealed record DUnidataRequest(uint Dst, bool Arq, int Priority, long Ttd, bool DeliverInOrder,
                                       int MinRetransmissions, ReadOnlyMemory<byte> CPdu, int Handle);
internal sealed record DExpeditedUnidataRequest(uint Dst, bool Arq, ReadOnlyMemory<byte> CPdu, int Handle);
internal sealed record DConnectionMade(uint Peer, DpduFamily Family, bool PeerIsEd4);
internal sealed record DConnectionTerminated(uint Peer, DReason Reason);

// DTS to CAS
internal sealed record DUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> CPdu);
internal sealed record DExpeditedUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> CPdu);
internal sealed record DUnidataRequestConfirm(int Handle);
internal sealed record DUnidataRequestRejected(int Handle, DReason Reason);
internal sealed record DWarningReceived(uint Src, uint Dst, int ReceivedFrameType, int Reason);
internal sealed record DWarningTransmitted(uint Dst, int ReceivedFrameType, int Reason);
internal sealed record DConnectionLost(uint Peer);

// SIS to CAS (B-0018)
internal sealed record CPhysicalLinkMake(uint Peer);
internal sealed record CPhysicalLinkBreak(uint Peer, SisBreakReason Reason);
internal sealed record CUnidataRequest(uint Dst, bool Arq, int Priority, long Ttd, bool DeliverInOrder,
                                       int MinRetransmissions, ReadOnlyMemory<byte> SPdu, int Handle);

// CAS to SIS
internal sealed record CPhysicalLinkMade(uint Peer);
internal sealed record CPhysicalLinkRejected(uint Peer, CReason Reason);
internal sealed record CPhysicalLinkBroken(uint Peer, CReason Reason);
internal sealed record CUnidataRequestConfirm(int Handle);
internal sealed record CUnidataRequestRejected(int Handle, CReason Reason);
internal sealed record CUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> SPdu);
```

`Handle` is our addition: the standard's request primitives carry no identifier, and the confirm and reject primitives of Table C-1 implicitly refer to "the request". An integer handle, allocated from one counter in the dispatcher, makes the reference explicit and deterministic. It never appears on the wire.

The MAC has no annex primitives (K-0002: CSMA needs no peer exchange). Its interface to the DTS is:

```csharp
internal interface IMacObserver            // DTS tells the MAC what it heard and what it holds
{
    void EotHeard(long predictedEndTime);  // from every valid header, C-0842, K-0021
    void DpduHeard(uint Src, uint Dst, bool DstIsGroup);   // for the two-node rule, K-0068, K-0072
    void QueueState(bool nonEmpty);        // K-0096: anything waiting to go on air
}
```

and the MAC asks the planner for a transmission when it enters LINKING (section 5).

### 4.2 Who owns what

| Sublayer | State it owns | Timers | Annex |
|---|---|---|---|
| **SIS** | Connections (`conn` -> `SisFrameDecoder`, bound SAP or none, flow state, invalid-primitive count); the SAP table (16 slots); per-connection pending U_PDUs awaiting a soft link, with TTDs; pending confirmation records (handle, deadline, dest, SAP, U_PDU prefix); soft link session records per peer (state, idle since, min hold until); the group membership list; the node's own address | `sis.confirm_wait`, `sis.ttd`, `sis.soft_link_idle/<peer>`, `sis.soft_link_min_hold/<peer>`, `sis.soft_link_max/<peer>` | A, S |
| **CAS** | The `PeerTable`: per peer address, link state (unlinked, calling, linked, closing, breaking), negotiated family (8-bit or 16-bit FSN), peer edition (unknown, ed3, ed4), request and break retry counts; the active link count; the mapping from handles to the DTS handles it issued | `cas.link_request/<peer>`, `cas.first_data/<peer>`, `cas.break/<peer>`, `cas.clean_close/<peer>` | B |
| **DTS** | The receive stream buffer (sync search state, per burst); per-peer `DtsPeer` records: the eight-state machine (C-0562), ARQ windows and per-FSN segment buffers both ways, reassembly state, management frame IDs, expedited state; the non-ARQ sender (type 7 and type 8 C_PDU ID counters, repeat counts); non-ARQ reassembly keyed by (src, dst, C_PDU ID, type); the transmit queue (section 5.2); the record of the transmission in flight; per-peer receive quality (SNR, lock, CRC counts) for phase 7 | `dts.ack_wait/<peer>`, `dts.management_wait/<peer>`, `dts.ttd`, `dts.reception_window` | C |
| **MAC** | The Annex K state (OFFLINE is never visible, SENSE, LBT_WAIT, CONT_WAIT, LINKING); the channel model (DCD, external busy, predicted EOT end time, which of DCD or VDCD cleared last); the last-transmission record (own or heard, and for a heard one its addressed-to-us-only evidence); the contention mode in force | `csma.lbt_wait`, `csma.cont_wait` | J, K |
| **Planner** | Nothing between transmissions. It reads the DTS queue, the PeerTable and the configuration and returns a built transmission | none | C.7.5 |

The DTS keeps its state per remote node (C-0565 to C-0568). The `PeerTable` is written by the CAS and read by the DTS and the planner (peer edition, family); the DTS writes its own `DtsPeer` records. Both are `SortedDictionary<uint, ...>` so enumeration order is address order (section 6).

### 4.3 One input, end to end

A `BytesReceived` carrying the last segment of a non-ARQ C_PDU for SAP 5:

1. `Node.Handle` sets `_now`, calls `Dts.OnBytesReceived(bytes)`.
2. The DTS appends the bytes to the burst buffer and searches for `90 EB` (C-0973, C-0365 resume rule). For each complete D_PDU with a valid header CRC, decoded with the peer edition from the `PeerTable` (ed4 unless the peer is known ed3; section 10):
   - `Mac.EotHeard(_now + eot * 500)` and `Mac.DpduHeard(src, dst, group)`, whatever the destination (C-0842, C-0868).
   - If the destination is our address, or a group we belong to, the D_PDU goes to `DtsPeer(src)`'s state machine (C-0566). Here Table C-10 or C-12 (type 7 received while unconnected, C-0585, C-0605): the reassembly for (src, C_PDU ID) completes, `dts.reception_window` is re-armed or cancelled, and `DUnidataIndication(src, dst, group, arq: false, cpdu)` is enqueued.
3. The FIFO drains: `Cas.On(DUnidataIndication)` checks the DATA C_PDU (B-0298, B-0325), strips the C_PCI and enqueues `CUnidataIndication(..., spdu)`.
4. `Sis.On(CUnidataIndication)` decodes the DATA S_PDU, checks the TTD (A-0241), finds the client bound to SAP 5 (or discards, A-0235), encodes `S_UNIDATA_INDICATION` and appends `SisBytesOut(conn, bytes)` to the outputs. If the S_PDU asked for client confirmation it also enqueues a `CUnidataRequest` carrying a DATA DELIVERY CONFIRM S_PDU back to the source (A-0238), which flows CAS to DTS and lands in the transmit queue; the DTS then calls `Mac.QueueState(true)`.
5. FIFO empty. `Mac.AfterInput()`: the channel is busy (DCD, and the predicted end time), so the MAC stays in SENSE with data queued (K-0066 waits for idle). No output.

Later, `BurstEnded(eom)` clears DCD and the EOT prediction (section 10), `AfterInput` sees idle with data queued, LBT rule 3 applies (the end was determined by VDCD, K-0071, K-0073), the MAC starts `csma.lbt_wait` with `lbt_wait_vdcd_ms` and moves to LBT_WAIT. At its expiry the MAC computes the contention wait (K-0080 to K-0082, drawing `uniform(num_cont_slots)` in jitter mode, prng.md site `csma.jitter`) and starts `csma.cont_wait`. At that expiry it enters LINKING, calls the planner, and emits `Transmit`. On `TransmitDone` it records "last transmission was ours", treats the end as VDCD-determined (K-0074), and returns to SENSE, or straight to LBT_WAIT if the queue is still non-empty.

---

## 5. The transmission planner

### 5.1 Role

Annex K decides *when* (one visit to LINKING is one transmission, K-0095). The planner decides *what*: it fills one transmission from the DTS queue within a duration budget, lays the D_PDUs out against the modem's block structure, stamps every EOT and reception window, and encodes the bytes. The planner is a pure function of (queue, PeerTable, config, `PlanBurst`, `_now`). It makes no PRNG draw, starts no timer and emits nothing itself; the MAC emits the `Transmit`.

### 5.2 The transmit queue

The DTS holds one queue of **transmit items**, each a ready-to-encode D_PDU less its EOT, reception window and CRCs:

```csharp
internal sealed class TxItem
{
    public TxClass Class;        // Management, Warning, CasControl, Ack, Retransmit, Data, NonArqRepeat (section 5.3 order)
    public int Priority;         // SIS priority 0 to 15 of the C_PDU it belongs to, 15 highest (A-0063, A-0064)
    public long Seq;             // allocation sequence number: the age tiebreak (C-0853)
    public uint Dst; public bool DstIsGroup;
    public uint? Peer;           // the ARQ peer, for family and window bookkeeping
    public Dpdu Pdu;             // EOT and reception window left at 0; the planner fills them
    public int HeaderBytes, SegmentBytes;   // sizes are known before EOT is, so layout can be done first
    public long? Ttd;            // for C-0857, C-0864
    public CPduRef Owner;        // the C_PDU this segment belongs to (adjacency rule C-0850, DROP C-0860)
}
```

Segmentation into items happens when the request arrives (Tables C-9 to C-24 say "segment the C_PDU and send all resulting D_PDUs"), with the segment size for that peer fixed at that moment (C-0549: not varied within a transmission; C-0550: may vary between). Block-aligned re-segmentation for Ed4 peers (C-0871, C-0872) is done by the planner as a last step over Data items that have never been sent (section 5.4), because only the planner knows the waveform of this transmission.

### 5.3 Build algorithm

```
Build(now):
  wn, il   = ChooseWaveform(addressed peers)              // phase 3: config drc.default_speed; phase 7: min over peers (C-0894)
  plan     = modem.PlanBurst(wn, il, 0)                   // gives preamble_ms, block_ms, bytes_per_block (section 11, change 1)
  budgetMs = min(127500, config.dts.max_transmission_ms)  // C-0106, K-0094, K-0095
  blocks   = (budgetMs - plan.PreambleMs - plan.FlushMs) / plan.BlockMs     // integer division, >= 1 (C-0551, C-0878)
  capacity = blocks * plan.BytesPerBlock

  chosen = []
  for class in [Management, Warning, CasControl, Ack, Retransmit, Data, NonArqRepeat]:
      for item in queue.Items(class) ordered by (Priority desc, Seq asc), subject to the class rules below:
          if item fits in the remaining capacity (with the adjacency rule for Data): take it
  Fill(chosen)                                             // section 5.5
  Layout(chosen, plan)                                     // byte offsets, block index of each D_PDU's first byte
  Stamp(chosen, plan)                                      // EOT, reception window (section 5.6)
  return Transmission(Encode(chosen), wn, il, k: 7, eom: true, items: chosen)
```

Class rules, each from its row:

- **Management**: at most one type 6 outstanding (C-0298), it goes first (C-0297). A pending type 6 ACK (VALID MESSAGE 0) also goes here.
- **Warning**: pending type 15s, oldest first; never one in answer to a WARNING (C-0393).
- **CasControl**: CAS-1 C_PDUs types 1 to 5 in type 8 D_PDUs (B-0191), ahead of data because the link they make or break gates the data (B-0280). Type 6 PHYSICAL LINK BREAK REQUEST travels as ordinary ARQ Data (B-0312).
- **Ack**: one ACK-ONLY per peer we hold unacknowledged receptions from (C-0370), and expedited acks (C-0277). If that peer also gets Data in this transmission, the planner merges the ack into the first DATA D_PDU to that peer as DATA-ACK (type 11 or 2) and drops the ACK-ONLY (C-0900 rationale; the state tables say "send ACK-ONLY or DATA-ACK as appropriate", C-0658).
- **Retransmit**: D_PDUs marked failed (C-0904), before first transmissions of equal or lower priority (C-0852, C-0905).
- **Data**: first transmissions by priority then age (C-0851, C-0853). The segments of one ARQ C_PDU are taken together and in FSN order; nothing of class Data is placed between them (C-0850, with the C-0855 reading that non-data classes may sit between). If a C_PDU's remaining segments do not fit, the planner takes as many leading segments as fit and the rest wait (the window rule C-0775 also caps how many new ARQ D_PDUs a peer may have outstanding).
- **NonArqRepeat**: further copies of non-ARQ D_PDUs whose minimum retransmission count is not yet met (A-0081, A-0083, C-0848, C-0884), spread through the transmission (C-0885: the planner interleaves repeats between other items rather than appending them, by placing the k-th repeat after the k-th quarter of the chosen list).

A C_PDU whose TTD has passed is skipped and rejected, by the DROP method for partly sent ARQ C_PDUs (C-0857 to C-0862) and by discarding for non-ARQ (C-0864); the DTS does this in the `dts.ttd` expiry and again at build time for anything that expired since.

### 5.4 Block alignment (Ed4 peers only)

For an item of class Data addressed to a peer known Ed4 (C-0870, C-0871, C-0560), the planner adjusts segment sizes so no D_PDU straddles a modem block (C-0872): if a D_PDU would cross a block boundary, its segment is shortened to end at the boundary and the remainder becomes a new segment with the next FSN (C-0553 allows short non-final segments to Ed4 peers). Towards Ed3 or unknown peers, and to any group or broadcast address, items are placed as they are and may straddle blocks (C-0554, C-0560); only the last segment of a C_PDU is short (C-0508).

### 5.5 Fill

Transmissions are whole modem blocks (C-0886). Spare bytes in the last block are filled, in this order: repeat the ACK-ONLY D_PDUs of this transmission while one fits (C-0882, C-0369); then, if every addressee of the transmission is a known Ed4 peer, PADDING D_PDUs carrying the current EOW recommendations (C-0367, C-0369); otherwise leave the rest to the modem's zero fill. EOT covers the fill (D-0005, D-0017).

### 5.6 EOT and reception window stamping

Let `D` be the planned duration from `PlanBurst(wn, il, totalBytes)`, `P` the preamble, `B` the block duration, and `b_i` the index of the block holding D_PDU i's first byte. The D_PDU's air start is taken as `s_i = P + b_i * B`, the start of its block (NOT STATED how EOT relates to an interleaved block; the start of the block is the earliest instant any of its bits is on air, so the EOT it yields is never early, C-0891; section 10).

```
eot_i    = ceilHalfSeconds(D - s_i)             // C-0104, C-0891; <= 255 because D <= 127500 (C-0106)
```

Because `s_i` is non-decreasing in i, `eot_i` is non-increasing (C-0890). The last D_PDU's EOT is at least 1 when D > s_last.

For a type 7 or 8 D_PDU, `reception_window_i` is the half seconds from `s_i` to the end of the last transmission that will carry a segment of the same C_PDU (C-0350, C-0352). When all remaining repeats are in this transmission, that is `ceilHalfSeconds(D - s_i)`. When repeats remain for later transmissions, NOT STATED; recommended reading in section 10.

### 5.7 After the build

The MAC emits `Transmit`. The DTS records the transmission in flight: for each ARQ item, FSN, peer and "sent at `_now`"; for non-ARQ items, the repeat count consumed. On `TransmitDone` the DTS starts `dts.ack_wait/<peer>` for each ARQ peer with unacknowledged D_PDUs (section 10, our timer), and the MAC does its K-0074 bookkeeping. If the planner returns nothing (everything expired), the MAC goes straight back to SENSE (K-0093, buffer empty).

---

## 6. Determinism rules for a byte-identical C++ port

These are checked in review of every core PR.

1. **Integer time, integer everything.** `long` milliseconds, `int` half seconds, `int` tenths of a dB. No `double`, `float`, `decimal`, `DateTime`, `TimeSpan` or `Stopwatch` in `M0LTE.S5066.Core`. Division only on non-negative operands, so C# and C++ truncation agree; where a value can be negative, test the sign first.
2. **Modular sequence arithmetic through one helper.** `SeqSpace` (modulus 256 or 65536) provides `Add`, `Distance(from, to)` and the arc test of C-0786. No ad hoc `%` on FSNs.
3. **No hash-order dependence.** Never enumerate a `Dictionary`, `HashSet` or `ConcurrentDictionary`. Per-key tables are `SortedDictionary` with a total order on the key: addresses by `uint`, `TimerName` by (Kind, Peer, Conn), handles by `int`. Lists that need ordering are sorted with `List<T>.Sort(Comparison)` whose comparison ends with a unique sequence number, because `List.Sort` is unstable; never rely on stability. LINQ `OrderBy` is allowed only with the same complete key.
4. **One PRNG, registered draw sites, fixed draw order.** The only generator is the `SplitMix64` created with the node (prng.md section 3). Phases 3 to 6 have exactly one site, `csma.jitter`, drawn in the CONT_WAIT computation of the LBT_WAIT expiry handler (K-0080, K-0084). Any new site is registered in prng.md section 6 with its row and its position relative to other sites reachable from the same input, in the PR that adds it. `uniform` is the only entry point.
5. **Run to completion, FIFO primitives, MAC last** (section 3.5). No sublayer holds a reference to another; all cross-sublayer traffic goes through the dispatcher.
6. **Outputs in production order, never buffered or merged.** One `SisBytesOut` per primitive, one `Transmit` per visit to LINKING, `TimerCancel` only for running timers.
7. **Decode with the phase 1 codecs only.** No second parser anywhere. The sync search feeds candidate slices to `DpduCodec.Decode` with the peer edition; the error kinds of corpus-format.md 3.1 decide what the stream reader does next.
8. **Bytes are immutable once produced.** `ReadOnlyMemory<byte>` in records; the planner encodes into fresh arrays. No shared mutable buffers across primitives.
9. **No exceptions for protocol events.** Malformed input from the air or a client is a protocol event with a row (WARNING, invalid primitive count, discard). Exceptions mean a bug (a timer expiry for a timer that is not running, a configuration the core cannot use), and the harness reports them as failures.
10. **Strings are for traces only.** `TimerName.ToTraceString`, address formatting and enum names exist for the harness and logs. No protocol decision compares or parses a string.
11. **Collections of peers are bounded and explicit.** Entries are created by named events (link request, first D_PDU heard, first SIS request) and removed by named events, never by a time-based sweep hidden inside another handler. A sweep is a timer with a registered name.
12. **No static mutable state.** Two `Node` instances in one process (the T3 simulation) must not share anything but code.

---

## 7. Configuration

### 7.1 The record

```csharp
public sealed record NodeConfig(
    uint LocalAddress,                 // local_address
    int AddressSize,                   // address_size, 1 to 7 (C-0133)
    IReadOnlyList<uint> Groups,        // groups: group addresses this node receives (A.2.2.9; A-0119)
    TimeConfig Time,                   // time.epoch_day, time.epoch_second (section 3.1)
    CsmaConfig Csma,                   // csma.*
    DtsConfig Dts,                     // dts.*
    CasConfig Cas,                     // cas.*
    SisConfig Sis,                     // sis.*
    DrcConfig Drc,                     // drc.*
    ModemFixture Modem);               // modem.* (T2 only; the host ignores it)

public sealed record CsmaConfig(CsmaMode Mode, int ContSlotWidthMs, int NumContSlots, int LbtWaitDcdMs,
                                int LbtWaitVdcdMs, int? NodeSlotPosition);
public enum CsmaMode { Jitter, Slotted, Ed3 }
```

The harness builds it from `defaults.json` plus the scenario overlay; the host builds it from its own file. The core is given the finished record. `prng_seed` is a constructor argument, not a field, so a run-time `config` input can never re-seed (prng.md section 3).

A setting that is `null` in `defaults.json` and not supplied by the scenario makes the harness fail the case with `missing_setting` before the core is constructed.

### 7.2 Settings, phases 3 to 6

Each is added to `defaults.json` and corpus-format.md section 6 by the PR that first needs it, with its source (corpus-format.md section 6 rule; plan 3.2 principle 5, no unrequested settings). The existing ones are `local_address`, `address_size`, `prng_seed`, `csma.*` and `modem.*`.

| Key | Default proposed | Source | Added by |
|---|---|---|---|
| `groups` | `[]` | A.2.2.9, A-0119 (group memberships are configuration, plan 8.2) | P3-5 |
| `time.epoch_day`, `time.epoch_second` | `1`, `0` | A-0162, A-0163, A-0166 (TTD needs a day and seconds; the core has no clock) | P3-5 |
| `csma.mode` | `null`, values `jitter`, `slotted`, `ed3` | D7; K-0107 coordinator ruling adds `ed3` | P3-2 (section 11, change 2) |
| `dts.max_segment_size` | 1023 | C-0503, C-0523 (configurable, at most 1023) | P3-4 |
| `dts.max_transmission_ms` | 127500 | K-0094, K-0095 (a smaller limit may be set by management) | P3-3 |
| `dts.ack_wait_ms` | `null` | ours (section 10, C-0911); derived from burst length plus turnaround in T4 (plan A4) | P4-2 |
| `dts.max_window` | 32768 | C-0772 (should be configurable), C-0773 | P4-2 |
| `drc.default_speed` | `{"wn": 2, "interleaver": "long"}` | plan 6.2 (conservative start; phase 7 adapts) | P3-3 |
| `cas.max_links` | `null` | B-0193, B-0209, B-0313 | P4-1 |
| `cas.link_request_timeout_ms`, `cas.link_request_retries` | `null` | B-0203, B-0204, B-0222 | P4-1 |
| `cas.first_data_timeout_ms` | `null` | B-0215, B-0216 | P4-1 |
| `cas.break_timeout_ms`, `cas.break_retries` | `null` | B-0283, B-0288 | P5-1 |
| `cas.clean_close_timeout_ms` | `null` | B-0322 | P5-1 |
| `sis.default_ttl` | 648000 | A-0087, A-0088, A-0091 (TTL 0 = default maximum = the field maximum, 2 s units) | P3-5 |
| `sis.frag_size` | `null` | A-0102, A-0114 (one value for confirms and rejects) | P6-1 |
| `sis.confirm_wait_ms` | `null` | A-0221, A-0226, A-0232 | P6-1 |
| `sis.soft_link_idle_ms`, `sis.soft_link_min_hold_ms`, `sis.soft_link_max_ms` | `null` | A-0011, A-0013, A-0015, A-0197 | P5-2 |

Fixed values the standard fixes or the matrix fixes as constants are not settings: MTU 2048 (A-0047), K = 7 and 3 kHz (plan 5.3), the invalid-primitive threshold of 10 (S-0077), the client write bound (S-0029, host side), port 5066 (host side, S-0007).

### 7.3 Run-time changes

`ConfigInput` replaces `NodeConfig` and calls each sublayer's `OnConfigChanged(old, new)`. The rule for when a change takes effect: a value is read when it is used, never cached, except where a cached copy is part of protocol state. Concretely: CSMA timer values apply to the next timer started; `csma.mode` applies to the next contention computation; `dts.max_segment_size` applies to C_PDUs segmented after the change; `local_address` and `address_size` apply to D_PDUs encoded after the change (a change mid-link is an operator error, not something the core guards against); `groups` applies to the next D_PDU received. No change restarts a running timer or re-seeds the PRNG.

---

## 8. The C# T2 scenario runner

Lives in `tests/M0LTE.S5066.Core.Tests`, replacing the `T2` skip in `CorpusHarnessTests`. One xunit theory row per scenario file, through `CorpusResults.Run` so the summary counts it.

```csharp
internal sealed class T2Runner
{
    // 1. header: merge config over defaults.json (ConfigMerge), build NodeConfig (ConfigReader); missing_setting on null
    // 2. new Node(config, seed, new FixtureModem(config.Modem)); outputs via node.DrainOutputs()
    // 3. harness timer table: name -> deadline, maintained from TimerStart and TimerCancel outputs
    // 4. for each input line: time checks (missed_timer), expiry check (bad_expiry), InputJson.Parse -> node.Handle
    //    -> DrainOutputs -> OutputJson.ToJson each -> compare with the following out lines (output_mismatch)
    // 5. stop at the first failure; report the reason words of harness-contract.md section 2
}
```

Pieces:

- `FixtureModem : IModemPort`: implements `PlanBurst` from the `modem` fixture by the arithmetic of corpus-format.md section 5 and fails with `unplanned_waveform` on a missing entry.
- `InputJson` and `OutputJson`: the JSON shapes of corpus-format.md 4.2 and 4.3 to and from the records of section 2. Comparison is `JsonNode.DeepEquals` after normalising both sides through the same serializer, so key order is irrelevant and types are exact. Byte fields are compared as bytes.
- `ConfigMerge`: the recursive overlay merge with the section 6 rules (unknown key, type mismatch are errors).
- The harness checks a `timer_expired` against its own table and also hands it to the core, which checks again (section 3.3). The two checks agreeing is itself a test of the core's timer bookkeeping.
- The harness reads `modem.preamble_ms`, `modem.flush_ms` and the waveform table and nothing else from the configuration for the fixture; it passes the whole `NodeConfig` to the core.

Authoring scenarios. Expected outputs are derived from the matrix rows, not recorded from the implementation. Hand-writing CRCs is impractical, so the tests project gets a small `ScenarioBuilder` that encodes expected D_PDUs, S_primitives and S_PDUs from field values with the phase 1 codecs (already byte-exact against T1), and emits the `out` lines. A scenario's `note` says which fields the bytes encode. A record mode that captures the implementation's outputs exists only as a diff aid for a reviewer and never writes into the corpus.

C++ later: the same scenario files, the same five failure words, a result file compared case by case (harness-contract.md section 5).

---

## 9. Work breakdown, phases 3 to 6

Two changes to the plan's phase contents, both forced by the T2 model and recorded here so the coordinator can update `docs/plan.md` section 12:

- **Annex K moves from phase 5 to phase 3.** Nothing can transmit without channel access, and the trace format already models it (`timer_expired csma.cont_wait` in the section 4.1 example). It is 111 rows with no PDUs; phase 5 keeps the Annex J analysis, the external busy input and the three-node simulation.
- **A thin SIS and CAS slice moves from phase 6 to phase 3, and CAS-1 link establishment from phase 5 to phase 4.** T2 inputs are modem, timer, config and SIS only (corpus-format.md 4.2). The only way a trace can give the node something to send is through the SIS, and ARQ needs `D_CONNECTION_MADE`, which only CAS-1 linking produces (C-0046 a priori knowledge does not apply). Adding an internal-primitive input to the trace format was considered and rejected: it would make the C++ port match an internal API, and the trace would stop being a statement about a real node.

Each item is one PR, reviewed by a fresh Opus reviewer, with its corpus scenarios in a conformance PR merged first (test-first). Rows are cited by range; the PR author lists the exact ids in each scenario's `rows`. Scenario areas follow corpus-format.md section 1.

### Phase 3: runtime, CSMA, planner, non-ARQ DTS, thin SIS and CAS

| Item | Contents | Rows | T2 scenarios (area/name) |
|---|---|---|---|
| **P3-0** this design, then: `Node`, `CoreInput`/`CoreOutput`, dispatcher FIFO, `TimerTable`, `TimerName`, `NodeConfig` and `ConfigReader`, `SeqSpace`, `FixtureModem`, `T2Runner`, `ScenarioBuilder`; a MAC stub that stays in SENSE | corpus-format.md 4, harness-contract.md 2 and 3 | K-0043, K-0063, K-0065 | `core/idle-sense` (burst_started, burst_ended, nothing queued, no outputs); `core/config-change` |
| **P3-1** D_PDU stream receiver | sync search over block boundaries, per-burst buffer reset, header CRC gate, type 13 resume rule, decode with peer edition, EOT to MAC, heard-address record, SNR and lock bookkeeping, data CRC discard | C-0973, C-0365, C-0829 to C-0838 (applicable), C-0839, C-0842, C-0843, D-0006, D-0007, K-0021 | `dts-rx/sync-search-junk`, `dts-rx/split-across-blocks`, `dts-rx/ext-type-resume`, `dts-rx/bad-header-crc`, `dts-rx/bad-data-crc` |
| **P3-2** Annex K CSMA | the four visible states and Tables 1 to 5, channel model (DCD, VDCD from EOTs, external busy), LBT rules 1 to 3, hold-while-busy, jitter, slotted and ed3 modes, own-last-transmission rule, LINKING to SENSE on transmit_done, `csma.jitter` draw site registered | K-0005, K-0019 to K-0021, K-0059 to K-0096, K-0104, K-0107, K-0109, K-0110; C-0841, C-0844, C-0889, C-0892 | `csma/sense-to-lbt-vdcd`, `csma/lbt-dcd-30s`, `csma/lbt-hold-while-busy`, `csma/jitter-draw` (seed 0 gives slot 15), `csma/slotted-position`, `csma/slotted-own-last-tx`, `csma/two-node-zero-lbt`, `csma/cont-wait-lost`, `csma/queue-emptied-to-sense`, `csma/ed3-mode`, `csma/external-busy` |
| **P3-3** transmission planner and EOT | sections 5.1 to 5.7 with class order, budget, block alignment, fill, EOT and reception window stamping, fixed waveform from `drc.default_speed`; `dts.max_transmission_ms` | C-0103 to C-0106, C-0549 to C-0555, C-0560, C-0848, C-0850 to C-0855, C-0870 to C-0872, C-0878, C-0880 to C-0892, C-0367 to C-0370, D-0003 to D-0005, D-0017, K-0093 to K-0095 | `dts-tx/eot-single-block`, `dts-tx/eot-monotonic-three-blocks`, `dts-tx/block-align-ed4`, `dts-tx/no-align-unknown-peer`, `dts-tx/fill-padding-ed4`, `dts-tx/budget-127500` |
| **P3-4** non-ARQ send | segmentation types 7 and 8, C_PDU ID spaces, offsets, repeats across and within transmissions, TTL expiry drop, DTS unconnected tables for requests (C-9, C-11, C-13, C-15), `dts.ttd` | C-0323 to C-0334, C-0338 to C-0357 (send side), C-0522 to C-0529, C-0541, C-0542, C-0857, C-0864, C-0570 to C-0576, C-0590 to C-0596, C-0610 to C-0616, C-0630 to C-0636 | `dts-nonarq/send-single-segment`, `dts-nonarq/send-three-segments`, `dts-nonarq/repeats-min-retx-2`, `dts-nonarq/ttl-expired-dropped`, `dts-nonarq/group-address` |
| **P3-5** thin SIS and CAS | connections, bind and unbind with reasons, SAP table, invalid primitive counting and unbind reason 3, keep-alive, pre-bind handling, unidata request validation (MTU, TTL cap, group with ARQ, invalid delivery mode), DATA S_PDU with TTD, DATA C_PDU wrap and unwrap, non-ARQ pass-through down and indication up, `groups`, `time.*`, `sis.default_ttl` | A-0030 to A-0058, A-0061 to A-0093, A-0116 to A-0125, A-0133, A-0143 to A-0168, A-0233 to A-0236; B-0012, B-0013, B-0042, B-0297, B-0298, B-0325; S-0009 to S-0016, S-0039 to S-0045, S-0074 to S-0077, S-0098 to S-0100, S-0102, S-0119, S-0121, S-0137, S-0138, S-0150, S-0157, S-0159 | `sis/bind-accept`, `sis/bind-sap-in-use`, `sis/bind-invalid-service-type`, `sis/unbind-rebind`, `sis/keep-alive`, `sis/invalid-primitives-unbind-3`, `sis/unidata-before-bind`, `sis/unidata-over-mtu`, `sis/unidata-group-arq-rejected`, `sis/nonarq-roundtrip` (request in, transmit out; bytes in, indication out) |
| **P3-6** non-ARQ receive and unconnected-state behaviour | reassembly, reception window expiry, delivery in arrival order, DELIVER IN ORDER ignored, group delivery; WARNING generation and reasons, addressed-to-us scope, never warn on warning; MANAGEMENT type 6 receive, ack, frame IDs, `dts.management_wait`; UNRECOGNIZED-TYPE-ERROR EOW; `dts.reception_window` | C-0336, C-0337, C-0530 to C-0540, C-0376 to C-0395, C-0577 to C-0589, C-0597 to C-0609, C-0617 to C-0629, C-0637 to C-0649, C-0293 to C-0321, C-1014 to C-1016, C-0444 to C-0448, C-0730 to C-0735 | `dts-nonarq/rx-two-segments-out-of-order`, `dts-nonarq/rx-window-expires`, `dts-nonarq/rx-group-member`, `dts-nonarq/rx-not-addressed-ignored`, `dts-nonarq/warning-reason-1-arq-while-unconnected`, `dts-nonarq/warning-reason-0-unknown-type`, `dts-nonarq/no-warning-on-warning`, `dts-nonarq/management-ack`, `dts-nonarq/management-repeat-same-id`, `dts-nonarq/eow-unrecognised-type-3` |
| **P3-7** T3 simulation harness | two or three `Node`s over a seeded lossy channel model in the tests project; broadcast to two receivers with loss; assertions on delivery and on no transmission overlapping an announced EOT | matrix `tests` column: C-0844, C-0889, A-0081 | none (T3) |

Phase 3 done when: every scenario above passes, the T3 broadcast test passes, and `docs/status.md` records it.

### Phase 4: CAS-1 linking and ARQ

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P4-1** CAS-1 link establishment and SIS soft link setup | Caller and Called procedures, request timer and retries, first-data timer, repeat request answered, Ed4 and Short negotiation, Ed3 fallback on WARNING, PeerTable edition record, `D_CONNECTION_MADE`, `link_state` registry (`unlinked`, `calling`, `linked`), soft link established on both sides, SIS queue until linked, `cas.max_links` reject reason 4, LINK bit reject reason 5 | B-0190 to B-0232 (applicable), B-0303 to B-0308, B-0313 to B-0315; A-0008, A-0010, A-0192 to A-0194; C-0043 to C-0056 (applicable), C-0571, C-0591, C-0611, C-0631 | `cas/make-caller-accepted`, `cas/make-called-accept`, `cas/make-called-too-many-links`, `cas/make-timeout-retry-then-fail`, `cas/make-repeat-request-answered`, `cas/ed3-peer-warning-fallback`, `cas/short-family-negotiated`, `cas/link-bit-rejected-5` |
| **P4-2** ARQ data path, 16-bit family | windows and edges, FSN allocation, DATA-ONLY, ACK-ONLY bitmap, DATA-ACK, reassembly, in-order delivery flag, DROP, confirm and reject to CAS, CONNECTED-state tables for IDLE and DATA, failed D_PDU marking and retransmission, half-duplex failed rule, `dts.ack_wait`, `dts.max_window` | C-0502 to C-0517, C-0557, C-0558, C-0763 to C-0785, C-0787, C-0650 to C-0689, C-0850 to C-0866, C-0904 to C-0911, C-0170 to C-0193 and C-0195 to C-0250 (bitmap parts), C-0866 | `dts-arq/data-ack-roundtrip`, `dts-arq/window-full-waits`, `dts-arq/retransmit-after-bitmap`, `dts-arq/ack-wait-expiry-retransmit`, `dts-arq/deliver-in-order`, `dts-arq/drop-pdu-ttl`, `dts-arq/data-ack-merge` |
| **P4-3** acknowledgement encodings | type 10 long form with its CRC, run-length encoding, type 11 encoding bit, offset | C-0195 to C-0235 (encoding parts), C-0237 to C-0250 | `dts-arq/ack-rle-long-run`, `dts-arq/ack-long-form-crc`, `dts-arq/ack-offset` |
| **P4-4** synchronisation and reset | Tests 1 to 5 on the arc model, FULL RESET procedure both roles, RESET/WIN-RESYNC receive handling, RESET FRAME ID | C-0253 to C-0274, C-0789 to C-0827 | `dts-arq/sync-test-1-fail-full-reset`, `dts-arq/full-reset-responder`, `dts-arq/full-reset-initiator`, `dts-arq/reset-frame-id-repeat-ignored`, `dts-arq/eq5-nothing-outstanding` |
| **P4-5** Ed3 family 0 to 3 and Short | the 8-bit family on a link negotiated Short or to an Ed3 peer, modulus 256, no PADDING and no Ed4 EOWs towards Ed3 | C-0052, C-0059 to C-0169 (family parts), C-0558, C-0730, C-0054, C-0367, B-0232 | `dts-arq/ed3-family-roundtrip`, `dts-arq/ed3-no-padding`, `dts-arq/short-family-window-128` |
| **P4-6** expedited and management in CONNECTED states | type 4 receive and type 5 ack, EXPEDITED-DATA(CONNECTED) exits, MANAGEMENT(CONNECTED), queued requests released, allowed-type lists | C-0276 to C-0291, C-0755, C-0690 to C-0729, C-0736 to C-0738, C-0740 to C-0751 | `dts-arq/expedited-rx-ack`, `dts-arq/management-connected-then-data`, `dts-arq/queued-request-released` |
| **P4-7** T3 ARQ | two nodes with loss, duplication and reordering; a long transfer completes; windows stay in sync | matrix `tests` column: C-0764, C-0790 | none (T3) |

### Phase 5: link break, sessions, Annex J, three nodes

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P5-1** link break and clean close | Initiator and Responder, break timer and retries, type 5 to unknown peers, clean close with type 6 and its reasons, clean close timeout, `D_CONNECTION_LOST`, rejecting unprocessed C_PDUs, `link_state` `closing`, `breaking` | B-0235 to B-0254, B-0275 to B-0289, B-0302, B-0309 to B-0312, B-0316 to B-0324; C-0652, C-0672, C-0692, C-0712, C-0739, C-0866 | `cas/break-initiator`, `cas/break-responder`, `cas/break-timeout-declared`, `cas/clean-close-no-data`, `cas/clean-close-higher-priority`, `cas/break-from-unknown-answered`, `cas/connection-lost-rejects-cpdus` |
| **P5-2** SIS soft link sessions | idle timeout, min hold on the called side, max session when other peers wait, break reasons and the rejections they cause | A-0011 to A-0015, A-0195 to A-0207, A-0200 | `sis-session/idle-break`, `sis-session/min-hold-called`, `sis-session/max-session-other-peer`, `sis-session/reject-reason-matches-break` |
| **P5-3** Annex J analysis | WARNINGs for EXTENSION and WTRP D_PDUs addressed to us, type 6 unknown subtype (WARNING plus EOW type 3), reporting to the operator via S_MANAGEMENT_MSG_INDICATION | J-0001, J-0029 to J-0054, J-0070, C-0735, C-0392 | `mac/extension-warning-reason-4`, `mac/wtrp-to-group-no-warning`, `mac/type6-unknown-subtype` |
| **P5-4** external busy input | `channel_busy` ORed into DCD, idle only when both clear | K-0006, K-0073 | `csma/external-busy-holds-lbt` |
| **P5-5** three-node T3 | two links plus broadcast; no transmission overlaps an announced EOT; slotted and jitter both | matrix `tests` column: K-0089, K-0090, C-0889 | none (T3) |

### Phase 6: SIS completion and host

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P6-1** delivery confirmation | node confirm from `C_UNIDATA_REQUEST_CONFIRM`, client confirm with DATA DELIVERY CONFIRM and FAIL S_PDUs, confirm timers (collapsed), TTD on receive, frag size, reason mapping, over-MTU reject | A-0094 to A-0115, A-0169 to A-0185, A-0214 to A-0232, A-0237 to A-0243 | `sis-confirm/node-confirm`, `sis-confirm/client-confirm-roundtrip`, `sis-confirm/delivery-fail-sap-unbound`, `sis-confirm/timeout-reason-3`, `sis-confirm/frag-size-copy`, `sis-confirm/ttd-passed-discard` |
| **P6-2** flow control and management | S_DATA_FLOW_ON and OFF policy, discard after FLOW_OFF, subnet availability, S_MANAGEMENT_MSG node information encoding, priority precedence across clients, shutdown unbind reason 6 | S-0019 to S-0038, S-0046 to S-0053, A-0059, A-0064 | `sis/flow-off-discard-rejected`, `sis/management-node-info`, `sis/priority-precedence` |
| **P6-3** host and client | the 5066 server in pdn-soundmodem (D3), `M0LTE.S5066.Client`, chat sample (Annex O); the host's same-millisecond class ordering (corpus-format.md 4.5) | S-0007 to S-0010 (host), client duty rows | none (host code, client duty tests) |
| **P6-4** T4 | two hosts over the audio simulation exchange chat both ways | plan 12 phase 6 done-when | none (T4) |

Twenty-four items. The scenarios named are the minimum; reviewers should ask for an edge case per interp row touched.

---

## 10. Choices the standard does not make (NOT STATED)

Each is an implementation choice a later item will need. Where a matrix row exists it is named; where none does, the implementing PR adds an `interp` row to the matrix in the conformance repository with the reading below, unless its reviewer finds a better one.

| # | Choice | Reading proposed here | Row |
|---|---|---|---|
| 1 | START and STOP of Annex K come from "subnetwork management", undefined | Construction is START (SENSE at time 0). STOP is the host destroying the core. No trace input. | K-0058, K-0062 (notes) |
| 2 | How a receiver turns a parsed EOT into an end time, and how several are combined | Each valid header gives `t_rx + eot * 500`; each is an upper bound on the true end (EOT rounds up and the bytes arrive after the D_PDU started), so the estimate is the minimum over the headers of the current burst, reset at `burst_started` | C-0842, C-0843, K-0021 (new interp) |
| 3 | Whether `burst_ended` ends the VDCD prediction | `eom` clears the prediction (the transmission verifiably ended); `signal_lost` and `terminated` keep it until the predicted time. A tie counts as VDCD (K-0073) | K-0073 (extend) |
| 4 | The EOT of a D_PDU inside an interleaved block | Air start of a D_PDU = start of the block holding its first byte, `P + b_i * B`; never early, so EOT is never early | C-0104, C-0891 (new interp) |
| 5 | Reception window when repeats span later transmissions | Place all repeats in one transmission when they fit (spread, C-0885). If not, window = this transmission's remaining time plus, per further transmission needed, `max_transmission_ms + lbt_wait_vdcd_ms + num_cont_slots * cont_slot_width_ms` | C-0350, C-0352, C-0884 (new interp) |
| 6 | When to retransmit if no acknowledgement ever arrives | `dts.ack_wait/<peer>` started at `transmit_done`, value `dts.ack_wait_ms`; expiry marks every unacknowledged D_PDU to that peer failed (the C-0911 "retransmit" branch) | C-0904, C-0911 (new interp); plan A4 |
| 7 | Fill order of a transmission across classes | Section 5.3 order: management, warning, CAS control, acks, retransmissions, new data, non-ARQ repeats, then fill | C-0855 (extend), C-0297, C-0288, B-0280 |
| 8 | Decoding types 9 to 14 from a peer whose edition is unknown | Decode as Ed4 (`peer_edition` ed4). Only a peer recorded Ed3 gets the Ed3 reading, where types 9 to 14 draw WARNING reason 0 | C-0051, C-0388 (new interp) |
| 9 | What the MAC may learn from D_PDUs addressed to others | Only the EOT and the (src, dst, group) triple for the channel model and the two-node rule; no state machine runs for them | C-0868, K-0072 |
| 10 | Request identity across the internal primitive interfaces | An integer handle allocated by the dispatcher; never on the wire | C-0039, B-0018 (notes) |
| 11 | Timer values for CAS linking and breaking, confirmation waits, soft link times | Configurable with no default (`null` in `defaults.json`); scenarios supply them; T4 derives recommended values (plan A4) | B-0203, B-0283, A-0221, A-0226, A-0197 |
| 12 | Where TTD wall-clock comes from in a core with no clock | `time.epoch_day` and `time.epoch_second` in configuration; the host sets them at start-up | A-0162, A-0163, A-0166 (new interp) |
| 13 | Station-layer identification hook transmissions (D4, K-0007) | Not in phases 3 to 6. Phase 8 will need an input (hook requests a turn, with its duration) and an output (turn granted) so the hook obeys Annex K from inside the core. Both are corpus-format additions for that phase | K-0007 |
| 14 | `terminate_receive` and `set_max_input_blocks` | Never emitted in phases 3 to 6 | plan 5.1 |
| 15 | Merging an ACK into a DATA D_PDU | When a peer receives both an acknowledgement and new data in one transmission, the first data D_PDU to it becomes DATA-ACK and the ACK-ONLY is dropped; ACK-ONLY repeats in fill still refer to the same RX LWE | C-0658, C-0678 ("as appropriate") |

---

## 11. Proposed changes to the corpus format and harness contract

To be made in the conformance repository (minor version bump before 1.0.0) alongside P3-0.

1. **corpus-format.md section 5, and harness-contract.md section 2 item 2.** State that the `PlanBurst` answer exposes `preamble_ms`, `block_ms`, `flush_ms`, `bytes_per_block`, `blocks` and `duration_ms`. Reason: the core needs the per-block start times to stamp a different EOT on each D_PDU (section 5.6) and the block capacity to size a transmission (section 5.3); today the text gives only the derived duration. The fixture already holds every value, so no file changes. Also say that the fake modem's `Transmit` return value may be ignored by a core that plans with `PlanBurst`; the `transmit` output is the observable.
2. **corpus-format.md section 6, `csma.mode`.** Add `ed3` as a third value, per the K-0107 coordinator ruling (ed3-compatible: jitter, one common LBT value equal to `lbt_wait_dcd_ms`, LBT rule 1 off).
3. **corpus-format.md section 4.6.** Register the timer names of section 3.2 and the `link_state` names of section 2.2 as their PRs land (no format change; listed so the registry grows in the order of section 9).
4. **prng.md section 6.** Fill the TODO for `csma.jitter` with K-0080 and K-0084 in P3-2.
5. **New settings** of section 7.2, each in the PR that first needs it, as the format already requires.

No change is proposed to the input or output kinds. The ten inputs and eight outputs are sufficient for phases 3 to 6; phase 8 adds the station hook pair (section 10 item 13).
