# Core architecture: the sans-IO runtime of M0LTE.S5066.Core

Design for issue #18 (P3-0). Version 1.1, 2026-11-07 (reviewer revision). This is the runtime every sublayer is built on in phases 3 to 6, by other agents. It fixes the shapes that must agree between the C# core and the later C++ port: the input and output types, the time and timer model, how the sublayers compose, how a transmission is planned, and the determinism rules. It does not restate protocol behaviour; that comes from the matrix rows each work item cites.

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
9. Work breakdown, phases 3 to 7
10. Choices the standard does not make (NOT STATED)
11. Proposed changes to the corpus format and harness contract
12. The host: driving the core in real time (P6-3a), with the client library and chat sample (12.10) and s5066d on a radio (12.11)

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
- `LinkState` names are registered when the CAS lands (P4-1, P5-1). P4-1 registered `unlinked`, `calling` and `linked`; P5-1 registered `closing` (clean close awaited) and `breaking` (this node the Initiator of the break).
- One `SisBytesOut` per encoded primitive. The core never concatenates two primitives into one output, so a trace reads one primitive per line and the C++ port has no buffering decisions to match.

### 2.3 Addresses

Addresses are `uint` 28-bit values throughout the core (`DpduAddress.MaxValue`), with the group flag carried separately where a PDU carries it (C-0122). The dotted form exists only in the harness and the host. A peer is identified by its unicast address; group addresses never key a per-peer table.

The size-of-address field m is not configured. The planner gives each D_PDU it sends the smallest m that holds both its destination and this node's address (`DpduAddress.MinimumSize`), so a broadcast D_PDU has m = 7 and a node with a short address can still send to any group or wider address (C-0133, C-0130; issue #35). A receiver accepts any m from 1 to 7.

---

## 3. Time and timers

### 3.1 Time

- Time is `long` milliseconds since the core was created (time 0), corpus-format.md 4.4. The core's clock is the `T` of the input it is handling: `Node.Handle` stores it in `_now`, and every sublayer reads `_now` through the dispatcher. No sublayer stores a time it did not get from an input or compute from one.
- The core never converts time to or from wall-clock. The one place the standard needs wall-clock, the TTD field of a DATA S_PDU (A-0162, A-0163, A-0166, A-0167), is handled by the SIS as follows: the host supplies, in configuration, the UTC year, day-of-year and seconds-of-day that correspond to core time 0 (`time.epoch_year`, `time.epoch_day`, `time.epoch_second`, section 7). The core adds `_now` to that with integer calendar arithmetic. The year is needed because day numbers restart after day 365 or 366 (A-0161) and the A-0167 resolution window crosses year ends. Internally every TTD is an absolute core time in ms; only the S_PDU codec boundary converts. In T2 the values come from `defaults.json`, so the TTD bytes in a trace are deterministic.
- Half seconds (EOT, reception window) are computed with integer arithmetic on milliseconds: `ceilHalfSeconds(ms) = (ms + 499) / 500`.

### 3.2 Timer names

```csharp
public enum TimerKind
{
    // csma
    CsmaLbtWait, CsmaContWait, CsmaVdcdEnd,
    // dts
    DtsAckWait /*peer*/, DtsManagementWait /*peer*/, DtsTtd, DtsReceptionWindow,
    // cas
    CasLinkRequest /*peer*/, CasFirstData /*peer*/, CasBreak /*peer*/, CasCleanClose /*peer*/,
    // sis
    SisConfirmWait, SisTtd, SisSoftLinkIdle /*peer*/, SisSoftLinkMinHold /*peer*/, SisSoftLinkMax /*peer*/,
    SisHeldOff /*peer*/,   // added by the #73 fix (interp B-0451)
}

/// A registered name: the kind plus its qualifier (a peer address or a conn), or none.
public readonly record struct TimerName(TimerKind Kind, uint? Peer = null, int? Conn = null)
{
    public string ToTraceString();                 // "dts.ack_wait/0.0.1.2", corpus-format.md 4.6
    public static TimerName Parse(string s);       // harness side only
}
```

The string forms are exactly the `<sublayer>.<timer>[/<qualifier>]` names of corpus-format.md 4.6. Each is registered in that document's table by the PR that first starts it (section 9 says which). The list above is the planned set for phases 3 to 6; a PR may add one, with its row, but should first try the collapsing rule below.

**Collapsing rule (one timer per deadline set).** Qualifiers may only be a peer address or a conn number, so a per-request timer cannot be named. Where a sublayer has many deadlines of one kind (U_PDUs awaiting confirmation, queued C_PDUs with TTDs, non-ARQ reassemblies with reception windows) it keeps a sorted list of deadlines and runs one named timer at the head. On expiry it processes every entry whose deadline is at or before `_now`, in list order, then re-arms for the new head. The list order is a total order (deadline, then insertion sequence number), so both cores process entries identically. The named timer is started or restarted exactly when the head deadline changes (an insertion ahead of the head, a removal of the head, or an expiry), and cancelled when the list empties; an insertion behind the head produces no output. An entry whose deadline is already at or before `_now` when it is inserted is processed at once, never put in the list (the zero-duration rule of section 3.3).

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

Restart is `Start` on a running timer: one `TimerStart` output, new deadline (corpus-format.md 4.4). There is no pause: the Annex K "hold while busy" reading of K-0087 is implemented as cancel on busy and start on idle (with the value the LBT rules give for that idle, K-0120), which is what a trace shows.

**Zero-duration rule.** Several protocol values can be zero: LBT rule 1 sets the LBT wait to zero (K-0067), the jitter draw can give slot 0 (K-0080), and slotted position 1 after another node's transmission gives a zero contention wait (K-0081). The format forbids `ms` = 0, so a sublayer that would start a timer with 0 ms starts nothing, emits nothing, and immediately takes the action its expiry would have taken, within the same handler. So SENSE to LBT_WAIT (0) to CONT_WAIT (0) to LINKING to `transmit` can all happen inside one input, with the `csma.jitter` draw made on the way. Both cores must follow this; a core that started a 1 ms timer instead would fail every two-node scenario. A zero wait asked for a timer that is already running is a bug (the core throws): the harness would still hold the old deadline, so the sublayer cancels the timer explicitly first and the `timer_cancel` shows in the trace (P3-0 implementation note).

**No enumeration of the timer table.** Protocol code never iterates the `TimerTable` (for example "cancel every timer for this peer"). A sublayer that must stop several timers cancels them by name, in the order its code lists them, which the implementing PR fixes in its comments. This keeps the order of `timer_cancel` outputs independent of how `TimerKind` happens to be numbered.

### 3.4 Delivery of expiries

The harness or host feeds `TimerExpired(name)` at the deadline. The dispatcher looks up `name.Kind` and calls the owning sublayer's `OnTimer(name)`. Nothing fires inside the core on its own; this is what harness-contract.md section 2 means by "the harness must not fire timers by itself", read from the core's side.

### 3.5 Same-millisecond ordering

Across inputs, the order is fixed by the trace or the host in the class order of corpus-format.md 4.5: configuration, timer expiries (in the order their `timer_start` outputs appeared), modem events, SIS events. The core does not reorder; it handles each input to completion before the next.

Within one input, the core's own order is fixed by four rules, and they are the whole of the intra-input ordering contract:

1. **Fixed entry order.** Each input kind is handed to the sublayers in this order, each call running to completion before the next:

   | Input | Calls, in order |
   |---|---|
   | `config` | replace `NodeConfig`; `OnConfigChanged` on SIS, CAS, DTS, MAC (none emits; section 7.3) |
   | `timer_expired` | the owning sublayer's `OnTimer(name)` |
   | `burst_started` | DTS (new burst: reset the stream buffer and the per-burst EOT and heard-address records), then SIS (records that a burst is being received; no output), then MAC (DCD true) |
   | `bytes_received` | DTS |
   | `burst_ended` | DTS (drop any partial D_PDU, record quality, then the clean close check, then the Annex J channel analysis judges the burst, P5-3), then SIS (decides, in peer address order, each soft link idle wait that ran out during the burst, interp A-0320, issue #88), then MAC (DCD false) |
   | `transmit_done` | DTS (in-flight bookkeeping, section 5.7), then MAC (own transmission ended) |
   | `channel_busy` | MAC |
   | `sis_*` | SIS |

2. **Run to completion with a FIFO of primitives.** A sublayer never calls another sublayer directly. It enqueues a primitive on the dispatcher's FIFO (`Internal` queue) and returns. After the entry calls, the dispatcher drains the FIFO in order until empty. So the ordering of cross-sublayer effects is the order of enqueueing, which is the order the code is written in. Outputs are appended to the output list the moment a sublayer produces them. The one exception is the DTS-to-MAC observer interface (section 4.1): those calls are synchronous, only update the MAC's channel model and queue flag, and never produce an output or enqueue anything.
3. **Per-D_PDU processing order.** For a `BytesReceived` input, each D_PDU found in the bytes is processed completely, in byte order, before the next: (a) header-level effects (EOT to the MAC channel model, the heard-address record, SNR bookkeeping), (b) the per-peer DTS state machine, (c) any primitive it enqueues. Only then the next D_PDU.
4. **The MAC acts last.** The MAC produces outputs in exactly one place: `Mac.AfterInput()`, which the dispatcher calls once, after the FIFO is empty, for every input. Its entry calls and its `OnTimer` only record facts (DCD changed, own transmission ended, `csma.lbt_wait` expired); `AfterInput` then takes every transition those facts and the queue state allow, chaining through zero-duration waits (section 3.3), and emits the resulting `timer_start`, `timer_cancel` and `transmit` outputs in the order of the transitions. When it decides to enter LINKING it first calls `Dts.ExpireDue(_now)`, which rejects every queued C_PDU whose TTD is at or before `_now` (C-0857, C-0864) by enqueueing primitives; the dispatcher drains the FIFO (so any resulting `sis_bytes` come out here), and the MAC then re-reads the queue state: empty means SENSE (K-0091), otherwise it calls the planner and emits `transmit`. A transmission is built nowhere else, so everything an input causes to be queued is eligible for the same transmission (C-0881, as late as possible).

The same four rules bind the C++ port. They are not optional style; a port that let the SIS call the CAS directly, or let the MAC cancel a timer inside its `burst_started` handler, would produce outputs in a different order on the first scenario that exercises a round trip.

---

## 4. Composition: SIS, CAS, DTS, MAC

### 4.1 The internal interfaces

The primitives of Tables B-1 and C-1 become records on the dispatcher FIFO. Each carries the information the annexes list for it, no more (B-0017, C-0038: pass control information explicitly, never derived from U_PDU contents, B-0014, C-0036).

```csharp
// CAS to DTS (C-0039, C-0040, C-0041)
internal sealed record DUnidataRequest(uint Dst, bool DstIsGroup, bool Arq, int Priority, long Ttd, bool DeliverInOrder,
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
internal sealed record CUnidataRequest(uint Dst, bool DstIsGroup, bool Arq, int Priority, long Ttd, bool DeliverInOrder,
                                       int MinRetransmissions, ReadOnlyMemory<byte> SPdu, int Handle);

// CAS to SIS
internal sealed record CPhysicalLinkMade(uint Peer);
internal sealed record CPhysicalLinkRejected(uint Peer, CReason Reason);
internal sealed record CPhysicalLinkBroken(uint Peer, CReason Reason);
internal sealed record CUnidataRequestConfirm(int Handle);
internal sealed record CUnidataRequestRejected(int Handle, CReason Reason);
internal sealed record CUnidataIndication(uint Src, uint Dst, bool DstIsGroup, bool Arq, ReadOnlyMemory<byte> SPdu);
```

In P3-0 only the routing bases exist (`ToSis`, `ToCas`, `ToDts`, by destination); each record above is added by the item that first sends it, with the reason enums it needs (P3-1, P4-1).

`DstIsGroup` on the two request records was added by P3-1: a non-ARQ D_PDU to a group says so in its own header (C-0122), and the destination value alone cannot tell.

P5-1 added `CPhysicalLinkBreak(Peer, SisBreakReason, Priority)` and `DConnectionLost(Peer)` from Tables B-1 and C-1, and two primitives of ours for clean close, which B.7.2.4 says needs DTS co-operation but gives no primitive for (B-0421): `DCloseRequest(Peer, ClosePolicy, BreakRequest, Handle)` from the CAS, and `DLinkCleanState(Peer, Clean)`, the DTS's answer and its later report.

The fixes for issues #70 to #73 added two more of ours: `DPeerArqHeard(Peer)`, DTS to CAS, sent once per connection when the first regular ARQ D_PDU from the peer arrives, so the CAS can tell a repeated PHYSICAL LINK REQUEST from a new call (interp B-0452); and `DExpeditedWithdraw(Handle)`, CAS to DTS, which drops a link C_PDU of the CAS's own that no transmission has placed yet, so a PHYSICAL LINK REQUEST the procedure has moved past never goes on air (interp B-0453). P7-2 (issue #77) added `DCpduHeld(Peer, CPdu)`, DTS to CAS, sent each time a complete C_PDU received through regular ARQ stays held for in-order delivery behind a missing D_PDU, so the CAS can end the Called node's first-data wait on a DATA C_PDU received but not yet delivered (interp B-0460). The DTS queues it after any D_UNIDATA_INDICATION from the same D_PDU; one D_PDU cannot both release and newly hold a C_PDU (a D_PDU that releases held C_PDUs fills the gap before its own C_PDU too), so the order never shows in a trace, but a port keeps it.

`Handle` is our addition: the standard's request primitives carry no identifier, and the confirm and reject primitives of Table C-1 implicitly refer to "the request". An integer handle, allocated from one counter in the dispatcher, makes the reference explicit and deterministic. It never appears on the wire.

The MAC has no annex primitives (K-0002: CSMA needs no peer exchange). Its interface to the DTS is:

```csharp
internal interface IMacObserver            // DTS tells the MAC what it heard and what it holds; no outputs (section 3.5)
{
    void EotHeard(long predictedEndTime);  // from every valid header with EOT > 0 (EOT 0 carries no end time, C-0105); C-0842, K-0021
    void DpduHeard(uint Src, uint Dst, bool DstIsGroup);   // for the two-node rule, K-0068, K-0072
    void QueueState(bool nonEmpty);        // K-0096: anything waiting to go on air
}
```

`QueueState(true)` means the planner could place at least one item now (K-0096). ARQ data held back by a full window (C-0775), or C_PDUs waiting for a link to be made, do not count, so the MAC never wins contention only to find nothing it may send. The DTS calls it whenever that answer may have changed, and the MAC samples the last value in `AfterInput`.

and the MAC asks the planner for a transmission when it enters LINKING (section 5).

### 4.2 Who owns what

| Sublayer | State it owns | Timers | Annex |
|---|---|---|---|
| **SIS** | Connections (`conn` -> `SisFrameDecoder`, bound SAP or none, flow state, invalid-primitive count); the SAP table (16 slots); per-connection pending U_PDUs awaiting a soft link, with TTDs; pending confirmation records (handle, deadline, dest, SAP, U_PDU prefix); soft link session records per peer (state, idle since, min hold until); the group membership list; the node's own address | `sis.confirm_wait`, `sis.ttd`, `sis.soft_link_idle/<peer>`, `sis.soft_link_min_hold/<peer>`, `sis.soft_link_max/<peer>` | A, S |
| **CAS** | The `PeerTable`: per peer address, link state (unlinked, calling, linked, closing, breaking), negotiated family (8-bit or 16-bit FSN), peer edition (unknown, ed3, ed4), request and break retry counts; the active link count; the mapping from handles to the DTS handles it issued | `cas.link_request/<peer>`, `cas.first_data/<peer>`, `cas.break/<peer>`, `cas.clean_close/<peer>` | B |
| **DTS** | The receive stream buffer (sync search state, per burst); per-peer `DtsPeer` records: the eight-state machine (C-0562), ARQ windows and per-FSN segment buffers both ways, reassembly state, management frame IDs, expedited state; the non-ARQ sender (type 7 and type 8 C_PDU ID counters, repeat counts); non-ARQ reassembly keyed by (src, dst, C_PDU ID, type); the transmit queue (section 5.2); the record of the transmission in flight; per-peer receive quality (SNR, lock, CRC counts) for phase 7; the Annex J channel analysis (per-burst evidence per sender, last finding reported per node; P5-3) | `dts.ack_wait/<peer>`, `dts.management_wait/<peer>`, `dts.ttd`, `dts.reception_window` | C, J (channel analysis, P5-3) |
| **MAC** | The Annex K state (OFFLINE is never visible, SENSE, LBT_WAIT, CONT_WAIT, LINKING); the channel model (DCD, external busy, predicted EOT end time, which of DCD or VDCD cleared last); the last-transmission record (own or heard, and for a heard one its addressed-to-us-only evidence); the contention mode in force | `csma.lbt_wait`, `csma.cont_wait`, `csma.vdcd_end` | J (MAC modes), K |
| **Planner** | Nothing between transmissions. It reads the DTS queue, the PeerTable and the configuration and returns a built transmission | none | C.7.5 |

The DTS keeps its state per remote node (C-0565 to C-0568). The `PeerTable` is written by the CAS and read by the DTS and the planner (peer edition, family); the DTS writes its own `DtsPeer` records. Likewise the receive quality records (C-0833) are written by the DTS receiver and read by the SIS for the node information message (P6-2, interp S-0164), through `CoreContext.RxQuality`; a read, never a call, so the SIS answers within its own handler. Both are `SortedDictionary<uint, ...>` so enumeration order is address order (section 6).

### 4.3 One input, end to end

A `BytesReceived` carrying the last segment of a non-ARQ C_PDU for SAP 5:

1. `Node.Handle` sets `_now`, calls `Dts.OnBytesReceived(bytes)`.
2. The DTS appends the bytes to the burst buffer and searches for `90 EB` (C-0973, C-0365 resume rule). For each candidate it reads the source address straight from the address field (`DpduAddress.Read` on bytes 6 to 6+m, before any CRC check), looks up that source's edition in the `PeerTable` (ed4 unless the peer is recorded ed3; section 10 item 8), and calls `DpduCodec.DecodeFirst` with it. The edition must be known before decoding because it changes the header-size check for types 9 to 14. For each complete D_PDU with a valid header CRC:
   - `Mac.EotHeard(_now + eot * 500)` when EOT > 0, and `Mac.DpduHeard(src, dst, group)`, whatever the destination (C-0842, C-0868).
   - If the destination is our address, or a group we belong to, the D_PDU goes to `DtsPeer(src)`'s state machine (C-0566). Here Table C-10 or C-12 (type 7 received while unconnected, C-0585, C-0605): the reassembly for (src, C_PDU ID) completes, `dts.reception_window` is re-armed or cancelled, and `DUnidataIndication(src, dst, group, arq: false, cpdu)` is enqueued.
3. The FIFO drains: `Cas.On(DUnidataIndication)` checks the DATA C_PDU (B-0298, B-0325), strips the C_PCI and enqueues `CUnidataIndication(..., spdu)`.
4. `Sis.On(CUnidataIndication)` decodes the DATA S_PDU, checks the TTD (A-0241), finds the client bound to SAP 5 (or discards, A-0235), encodes `S_UNIDATA_INDICATION` and appends `SisBytesOut(conn, bytes)` to the outputs. If the S_PDU asked for client confirmation it also enqueues a `CUnidataRequest` carrying a DATA DELIVERY CONFIRM S_PDU back to the source (A-0238), which flows CAS to DTS and lands in the transmit queue; the DTS then calls `Mac.QueueState(true)`.
5. FIFO empty. `Mac.AfterInput()`: the channel is busy (DCD, and the predicted end time), so the MAC stays in SENSE with data queued (K-0066 waits for idle). No output.

Later, `BurstEnded(eom)` clears DCD. The EOT prediction is kept: the node may not transmit before it even if no signal is heard [Ed4 C.7.5.1.1, p.C-110; C-0844], and it normally lies a little after the burst end because EOT rounds up and is measured from when the bytes arrived (section 10 item 2). So `AfterInput` sees DCD clear, the prediction still in the future and data queued, and starts `csma.vdcd_end` for the predicted end time. At its expiry the channel becomes idle with VDCD the last to clear (K-0073), LBT rule 3 applies (K-0071), and the MAC starts `csma.lbt_wait` with `lbt_wait_vdcd_ms` and moves to LBT_WAIT. (Had the prediction already passed at `burst_ended`, or no EOT been parsed, DCD clears last and rule 2 gives `lbt_wait_dcd_ms`.) At the LBT expiry the MAC computes the contention wait (K-0080 to K-0082, drawing `uniform(num_cont_slots)` in jitter mode, prng.md site `csma.jitter`) and starts `csma.cont_wait`. At that expiry it enters LINKING, calls the planner, and emits `Transmit`. On `TransmitDone` it records "last transmission was ours", treats the end as VDCD-determined (K-0074), and returns to SENSE; the same `AfterInput` then applies the SENSE rule, so with data still queued and the channel idle it moves on to LBT_WAIT at once.

`csma.vdcd_end` runs exactly while all of these hold after an input: the MAC is in SENSE or LBT_WAIT, data is queued, DCD (with any external busy) is false, and the predicted end time is later than `_now`. Its deadline is the predicted end time. Since EOTs are parsed only during a burst, the prediction is fixed by the time DCD clears, so the timer is started once per received burst at most, and only when something is waiting to go.

---

## 5. The transmission planner

### 5.1 Role

Annex K decides *when* (one visit to LINKING is one transmission, K-0095). The planner decides *what*: it fills one transmission from the DTS queue within a duration budget, lays the D_PDUs out against the modem's block structure, stamps every EOT and reception window, and encodes the bytes. The planner is a pure function of (queue, PeerTable, config, `PlanBurst`, `_now`). It makes no PRNG draw, starts no timer and emits nothing itself; the MAC emits the `Transmit`.

### 5.2 The transmit queue

The DTS holds one queue of **transmit items**, each a ready-to-encode D_PDU less its EOT, reception window and CRCs:

```csharp
internal sealed class TxItem
{
    public TxClass Class;        // Management, Warning, CasControl, Expedited, Ack, Retransmit, Data, NonArqRepeat (section 5.3 order)
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

First transmissions of data are segmented by the planner at build time, not when the request arrives. A queued C_PDU is held whole, with a cursor marking how much of it has been placed; the planner cuts the next segments from the cursor using the maximum segment size in force for this transmission (C-0549: not varied within a transmission; C-0550: may vary between), shortened where block alignment asks (section 5.4). Tables C-9 to C-24 say "segment the C_PDU and send all resulting D_PDUs"; doing it at the last moment has the same external behaviour (C-0846) and is the only way block alignment and C-0550 can work. Consequences that both cores must follow:

- An ARQ FSN is allocated when a segment is first placed in a transmission, in placement order, from the peer's TX UWE (so the segments of one C_PDU get consecutive FSNs, C-0850), and only while the window allows (C-0775). An unplaced segment has no FSN.
- Non-ARQ segments are cut once, at the C_PDU's first placement, and every repeat is a copy of those segments (same C_PDU ID, offsets and sizes); only EOT and the reception window are restamped.
- Retransmissions keep the size and FSN they were built with (C-0549 notes).
- A `TxItem` therefore exists for each built D_PDU still owed (a retransmission or a non-ARQ repeat) and for each control D_PDU; data not yet placed is represented by its C_PDU and cursor, with `Seq` the C_PDU's arrival sequence number.

### 5.3 Build algorithm

```
Build(now):
  wn, il   = ChooseWaveform(addressed peers)              // phase 3: config drc.default_speed; phase 7: min over peers (C-0894)
  plan     = modem.PlanBurst(wn, il, 0)                   // gives preamble_ms, block_ms, bytes_per_block (section 11, change 1)
  budgetMs = min(127500, config.dts.max_transmission_ms)  // C-0106, K-0094, K-0095
  blocks   = (budgetMs - plan.PreambleMs - plan.FlushMs) / plan.BlockMs     // integer division
  if the first item the loop below would take does not fit in blocks:       // C-0551, C-0878, C-1042
      blocks = ceil(first.size / plan.BytesPerBlock)                        // just long enough for it (P3-4 departure, below)
      if plan.PreambleMs + blocks * plan.BlockMs + plan.FlushMs > 127500: configuration error
  capacity = blocks * plan.BytesPerBlock

  chosen = []
  for class in [Management, Warning, CasControl, Expedited, Ack, Retransmit, Data, NonArqRepeat]:
      for item in queue.Items(class) ordered by (Priority desc, Seq asc), subject to the class rules below:
          if item fits in the remaining capacity (with the adjacency rule for Data): take it
          else if class is Data or NonArqRepeat: stop this class      // C-0851: never let a lower priority overtake
          else: skip it and try the next
  Spread(chosen)                                           // NonArqRepeat placement, below
  Fill(chosen)                                             // section 5.5
  Layout(chosen, plan)                                     // byte offsets, block index of each D_PDU's first byte
  Stamp(chosen, plan)                                      // EOT, reception window (section 5.6)
  return Transmission(Encode(chosen), wn, il, k: 7, eom: true, items: chosen)
```

P3-4 departure: version 1.1 of this design let a transmission that had to exceed `dts.max_transmission_ms` for its first D_PDU fill up to 127.5 s. P3-4 makes it exactly the whole blocks that D_PDU needs, so other D_PDUs only use what is left of those blocks: the configured limit never blocks one D_PDU (C-0551) and is exceeded by no more than that D_PDU requires (C-1042). A D_PDU that needs more than 127.5 s on the waveform in use is a configuration the core cannot use.

P3-5 departure: the `Spread` formula below put the copies of each segment of a multi-segment C_PDU next to each other (three segments sent twice came out as S0 S0 S1 S1 S2 S2), which is the opposite of what C-0885 asks. P3-5 lays a transmission out as its first copies in the order chosen, then each round of repeats (every copy numbered 2, then every copy numbered 3, and so on, counting every transmission of that D_PDU so far) in the order chosen; when the repeated D_PDUs share one count, a whole round separates the copies of each D_PDU (C-1054). Selection of NonArqRepeat items goes C_PDU by C_PDU (priority, then Seq), and within a C_PDU lowest copy number first, then segment order; a misfit stops the class. The DTS also restarts or cancels `dts.ttd` at the end of a build, because the queue's earliest deadline may have changed, so a build can emit that one timer output just before the MAC's `transmit` (section 5.1 otherwise holds). Two more P3-5 readings bind the class: a C_PDU ID is not reused while the window of its previous holder is open, and a C_PDU needing a held ID stops its class (C-1053); and copies owed for a C_PDU are placed only in a transmission that, at its longest, ends within the window last announced for it, otherwise they are dropped (C-1052).

Class rules, each from its row:

- **Management**: at most one type 6 outstanding (C-0298), it goes first (C-0297). A pending type 6 ACK (VALID MESSAGE 0) also goes here.
- **Warning**: pending type 15s, oldest first; never one in answer to a WARNING (C-0393).
- **CasControl**: CAS-1 C_PDUs types 1 to 5 in type 8 D_PDUs (B-0191), ahead of data because the link they make or break gates the data (B-0280). Type 6 PHYSICAL LINK BREAK REQUEST travels as ordinary ARQ Data (B-0312).
- **Expedited**: client expedited traffic, type 4 first transmissions and retransmissions, type 5 EXPEDITED-ACK-ONLY (C-0277) and client type 8; never behind DATA-ONLY or DATA-ACK (C-0288). Ordered by Seq.
- **Ack**: one ACK-ONLY per peer we hold unacknowledged receptions from (C-0370), in peer address order. If that peer also gets Data in this transmission, the planner merges the ack into the first DATA D_PDU to that peer as DATA-ACK (type 11 or 2) and drops the ACK-ONLY (C-0900 rationale; the state tables say "send ACK-ONLY or DATA-ACK as appropriate", C-0658). The ACK-ONLY's size is reserved when it is taken; the merge happens when that Data item is taken and releases the reservation, and since a DATA-ACK costs less than a DATA-ONLY plus an ACK-ONLY the merge can never overflow the capacity.

P4-3 reading (C-1151): a peer's acknowledgement may take several ACK-ONLY D_PDUs. Each carries at most 32 bytes of Ack Data, the size the annex says the offset mechanism makes possible, so ACK-ONLYs stay small as it recommends (C-0213; Ed4 C.4.5.5, p.C-31). A window whose more compact encoding (C-1150) needs more is split at multiples of 256 FSNs, each ACK-ONLY covering the most whole 256-FSN units that fit; they go in Offset order, all of a peer's or none, and the fill repeats them (C-1154). Only a single ACK-ONLY of at most 18 bytes merges into a DATA-ACK (C-1152).
- **Retransmit**: D_PDUs marked failed (C-0904), before first transmissions of equal or lower priority (C-0852, C-0905).
- **Data**: first transmissions by priority then age (C-0851, C-0853). The segments of one ARQ C_PDU are taken together and in FSN order; nothing of class Data is placed between them (C-0850, with the C-0855 reading that non-data classes may sit between). If a C_PDU's remaining segments do not fit, the planner takes as many leading segments as fit and the rest wait (the window rule C-0775 also caps how many new ARQ D_PDUs a peer may have outstanding).
- **NonArqRepeat**: further copies of non-ARQ D_PDUs whose minimum retransmission count is not yet met (A-0081, A-0083, C-0848, C-0884), spread through the transmission (C-0885). P3-5 replaced the formula in the rest of this item with round order (departure above, C-1054). Formerly: `Spread` is exact: with `n` items chosen from the other classes, in order, and `r` repeats chosen, in order, repeat `j` (0-based) is inserted after the first `((j + 1) * n) / (r + 1)` other items (integer division); repeats sharing an insertion point keep their order. If an insertion point falls inside a run of first-transmission segments of one ARQ C_PDU, it moves to the end of that run (C-0850; a type 7 is data-bearing, so the C-0855 relaxation does not cover it).

A C_PDU whose TTD has passed is skipped and rejected, by the DROP method for partly sent ARQ C_PDUs (C-0857 to C-0862) and by discarding for non-ARQ (C-0864); the DTS does this in the `dts.ttd` expiry and again in `Dts.ExpireDue`, which `AfterInput` calls just before entering LINKING (section 3.5 rule 4). A TTD at or before `_now` counts as passed, in both places.

### 5.4 Block alignment (Ed4 peers only)

For an item of class Data addressed to a peer known Ed4 (C-0870, C-0871, C-0560), the planner adjusts segment sizes so no D_PDU straddles a modem block (C-0872): if a D_PDU would cross a block boundary, its segment is shortened to end at the boundary and the remainder becomes a new segment with the next FSN (C-0553 allows short non-final segments to Ed4 peers). Towards Ed3 or unknown peers, and to any group or broadcast address, items are placed as they are and may straddle blocks (C-0554, C-0560); only the last segment of a C_PDU is short (C-0508).

P4-3 departure (C-1155): alignment applies to first transmissions of ARQ data only. Non-ARQ type 7 and 8 segments are cut once, at first placement, and every repeat is a copy (C-0352), so they are laid out as cut. A C_PDU's last segment is never shortened, so a small C_PDU is never split into two D_PDUs only to avoid a boundary; a segment that would run past the transmission's capacity is cut to end at it, which fills the last block (C-0887 item 3). When the capacity was stretched past `dts.max_transmission_ms` only for the transmission's first D_PDU (C-1042) and alignment cuts that D_PDU to fewer blocks, the capacity shrinks back to the larger of the configured length and the blocks it now needs. "Known Ed4" means recorded Ed4 in the `PeerTable` by CAS-1 negotiation (B-0408), which stays after a link ends, or since P7-3 by a received CAPABILITY where CAS-1 has not spoken (interp C-1261); ARQ data always follows a CAS-1 negotiation, so for alignment only the CAS-1 record matters. ARQ C_PDUs are now cut one segment at a time, from a cursor, as each is placed.

### 5.5 Fill

Transmissions are whole modem blocks (C-0886). Spare bytes in the last block are filled, appended after the chosen items, in this exact order: (1) copies of the ACK-ONLY D_PDUs of this transmission, cycling through them in their order in the transmission, stopping at the first copy that does not fit (C-0882, C-0369); (2) then, if every addressee of the transmission is a known Ed4 peer, as many PADDING D_PDUs as fit, addressed to the lowest addressee address, carrying the EOW the DTS's EOW source gives (C-0367, C-0369; phase 7 decides the EOW content); (3) the rest is left to the modem's zero fill. EOT covers the fill (D-0005, D-0017). ACK-ONLY copies need a peer with a link, which is phase 4 onwards; a phase 3 non-ARQ transmission has no ACK-ONLY and no known Ed4 addressee, so it is zero-filled. Since P7-3 a non-ARQ transmission to a peer recorded Ed4 by a received CAPABILITY is padded (corpus drc-rx-capability-records-ed4).

P4-3 readings (C-1154): the fill uses only the spare bytes of the last block the chosen D_PDUs need and never adds a block. The ACK-ONLY copies include an acknowledgement a DATA-ACK carried (C-0240), as an ACK-ONLY, and a peer whose acknowledgement needs several ACK-ONLY D_PDUs (C-1151) has each copied in turn. PADDING goes when every addressee is recorded Ed4, by CAS-1 negotiation, which can be before the link is made (the Called node records the Caller's edition from its request, so its PHYSICAL LINK ACCEPTED transmission is padded), or since P7-3 by a received CAPABILITY where CAS-1 has not spoken (interp C-1261). Since P7-3 each PADDING takes the next EOW of its peer's sequence from the EOW scheduler (interp C-1260): an owed EOW first, then CAPABILITY while the repeating set is empty.

### 5.6 EOT and reception window stamping

Let `D` be the planned duration from `PlanBurst(wn, il, totalBytes)`, `P` the preamble, `B` the block duration, and `b_i` the index of the block holding D_PDU i's first byte. The D_PDU's air start is taken as `s_i = P + b_i * B`, the start of its block (NOT STATED how EOT relates to an interleaved block; the start of the block is the earliest instant any of its bits is on air, so the EOT it yields is never early, C-0891; section 10).

```
eot_i    = ceilHalfSeconds(D - s_i)             // C-0104, C-0891; <= 255 because D <= 127500 (C-0106)
```

Because `s_i` is non-decreasing in i, `eot_i` is non-increasing (C-0890). The last D_PDU's EOT is at least 1 when D > s_last.

For a type 7 or 8 D_PDU, `reception_window_i` is the half seconds from `s_i` to the end of the last transmission that will carry a segment of the same C_PDU (C-0350, C-0352). When all remaining repeats are in this transmission, that is `ceilHalfSeconds(D - s_i)`. When repeats remain for later transmissions, NOT STATED; recommended reading in section 10. Whatever window a D_PDU carries is a commitment: the sender never places a later segment or repeat of that C_PDU after the window it last stamped has run out (C-0353), it drops the owed repeats instead, and the C_PDU ID is not reused until the window has passed (C-0327).

### 5.7 After the build

The MAC emits `Transmit`. The DTS records the transmission in flight: for each ARQ item, FSN, peer and "sent at `_now`"; for non-ARQ items, the repeat count consumed. While in LINKING the MAC takes no transition and emits nothing; channel inputs only update its model, and anything queued meanwhile waits for the next transmission. On `TransmitDone` the DTS first starts `dts.ack_wait/<peer>` for each ARQ peer with unacknowledged D_PDUs, in ascending peer address order (section 10, our timer); then the MAC does its K-0074 bookkeeping and `AfterInput` applies the SENSE rule (section 4.3). The planner never returns an empty transmission, because `AfterInput` has already run the TTD sweep and checked the queue (section 3.5 rule 4).

---

## 6. Determinism rules for a byte-identical C++ port

These are checked in review of every core PR.

1. **Integer time, integer everything.** `long` milliseconds, `int` half seconds, `int` tenths of a dB. No `double`, `float`, `decimal`, `DateTime`, `TimeSpan` or `Stopwatch` in `M0LTE.S5066.Core`. Division only on non-negative operands, so C# and C++ truncation agree; where a value can be negative, test the sign first.
2. **Modular sequence arithmetic through one helper.** `SeqSpace` (modulus 256 or 65536) provides `Add`, `Distance(from, to)` and the arc test of C-0786. No ad hoc `%` on FSNs.
3. **No hash-order dependence.** Never enumerate a `Dictionary`, `HashSet` or `ConcurrentDictionary`. Per-key tables are `SortedDictionary` with a total order on the key: addresses by `uint`, `TimerName` by (Kind, Peer, Conn), handles by `int`. Lists that need ordering are sorted with `List<T>.Sort(Comparison)` whose comparison ends with a unique sequence number, because `List.Sort` is unstable; never rely on stability. LINQ `OrderBy` is allowed only with the same complete key.
4. **One PRNG, registered draw sites, fixed draw order.** The only generator is the `SplitMix64` created with the node (prng.md section 3). Phases 3 to 6 have exactly one site, `csma.jitter`: one `uniform(num_cont_slots)` per entry into CONT_WAIT in jitter or ed3 mode, made inside `AfterInput` at the moment of entry, whether the entry follows a `csma.lbt_wait` expiry or a zero LBT wait in the same input (K-0080, K-0084). Slotted mode never draws. Any new site is registered in prng.md section 6 with its row and its position relative to other sites reachable from the same input, in the PR that adds it. `uniform` is the only entry point.
5. **Fixed entry order, run to completion, FIFO primitives, MAC acts last** (section 3.5). No sublayer holds a reference to another; all cross-sublayer traffic goes through the dispatcher, except the output-free DTS-to-MAC observer calls.
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
    IReadOnlyList<uint> Groups,        // groups: group addresses this node receives (A.2.2.9; A-0119)
    TimeConfig Time,                   // time.epoch_year, time.epoch_day, time.epoch_second (section 3.1)
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

The record above is the full set for phases 3 to 6; the code holds only the fields whose keys exist in `defaults.json`, and each item of section 7.2 adds its own. `ConfigReader`, which turns the merged JSON object into the record, is public in `M0LTE.S5066.Core` so the host can share it; `Node` never sees JSON, and `ConfigMerge` stays in the tests project. The harness builds it from `defaults.json` plus the scenario overlay; the host builds it from its own file. The core is given the finished record. `prng_seed` is a constructor argument, not a field, so a run-time `config` input can never re-seed (prng.md section 3).

A setting that is `null` in `defaults.json` and not supplied by the scenario fails the case with `missing_setting` when the core needs it. `local_address` is needed by every node, so `ConfigReader` checks it before the core is constructed. Settings only some behaviour needs (`csma.mode`, `csma.node_slot_position`, `modem.preamble_ms`, `modem.flush_ms`) stay nullable in the record and throw `MissingSettingException` where they are read, because corpus-format.md section 6 makes a null a failure only for a scenario that exercises the setting (P3-0 implementation note).

### 7.2 Settings, phases 3 to 7

Each is added to `defaults.json` and corpus-format.md section 6 by the PR that first needs it, with its source (corpus-format.md section 6 rule; plan 3.2 principle 5, no unrequested settings). The existing ones are `local_address`, `prng_seed`, `csma.*` and `modem.*`.

| Key | Default proposed | Source | Added by |
|---|---|---|---|
| `groups` | `[]` | A.2.2.9, A-0119 (group memberships are configuration, plan 8.2) | P3-1 |
| `time.epoch_year`, `time.epoch_day`, `time.epoch_second` | `2026`, `1`, `0` | A-0161, A-0162, A-0163, A-0166, A-0167 (TTD needs a day and seconds, and the year for day-number wrap; the core has no clock) | P3-1 |
| `csma.mode` | `null`, values `jitter`, `slotted`, `ed3` | D7; K-0107 coordinator ruling adds `ed3` | P3-2 (section 11, change 2) |
| `dts.max_segment_size` | 1023 | C-0503, C-0523 (configurable, at most 1023) | P3-4 |
| `dts.max_transmission_ms` | 127500 | K-0094, K-0095 (a smaller limit may be set by management; never applied to block one D_PDU, C-0551) | P3-4 |
| `dts.ack_wait_ms` | `null` | ours (section 10, C-0911, C-1120); null means derived: `dts.max_transmission_ms` plus the longer of `lbt_wait_dcd_ms` and `lbt_wait_vdcd_ms` plus `num_cont_slots * cont_slot_width_ms`, the longest a peer with our settings takes to answer; T4 may recommend a value (plan A4) | P4-2 |
| `dts.max_window` | 32768 | C-0772 (should be configurable), C-0773 | P4-2 |
| `drc.default_speed` | `{"wn": 2, "interleaver": "long"}` | plan 6.2 (conservative start; phase 7 adapts) | P3-4 |
| `cas.max_links` | `null` | B-0193, B-0209, B-0313 | P4-1 |
| `cas.link_request_timeout_ms`, `cas.link_request_retries` | `null` | B-0203, B-0204, B-0222; the timeout derived when null, one answer wait (B-0420, P5-1) | P4-1 |
| `cas.first_data_timeout_ms` | `null` | B-0215, B-0216; derived when null, three answer waits (B-0420, P5-1) | P4-1 |
| `cas.break_timeout_ms`, `cas.break_retries` | `null` | B-0283, B-0288, B-0424; the timeout derived when null, one answer wait (B-0420) | P5-1 |
| `cas.clean_close_timeout_ms` | `null` | B-0322; derived when null, three answer waits (B-0420) | P5-1 |
| `sis.default_ttl` | 648000 | A-0087, A-0088, A-0091 (TTL 0 = default maximum = the field maximum, 2 s units) | P3-1 |
| `sis.frag_size` | `null` | A-0102, A-0114 (one value for confirms and rejects) | P6-1 |
| `sis.confirm_wait_ms` | `null` | A-0221, A-0226, A-0232 | P6-1 |
| `sis.soft_link_idle_ms`, `sis.soft_link_min_hold_ms`, `sis.soft_link_max_ms` | `null` | A-0011, A-0013, A-0015, A-0197; derived when null, three, three and six answer waits (A-0321) | P5-2 |
| `drc.table` | an empty table: no calibration, no speed recommendations | plan 6.2; calibration data, not an operator setting: T2 scenarios supply synthetic tables, the host loads the calibrated one for its modem from s5066-conformance `calibration/<modem>-<version>/` (section 9 phase 7, design shape items 5 and 6); an empty table also means fixed speed at `drc.default_speed`, the baseline (no separate fixed-speed setting, open question 13); schema in corpus-format.md section 6 | P7-6 |

Fixed values the standard fixes or the matrix fixes as constants are not settings: MTU 2048 (A-0047), K = 7 and 3 kHz (plan 5.3), the invalid-primitive threshold of 10 (S-0077), the client write bound (S-0029, host side), port 5066 (host side, S-0007).

### 7.3 Run-time changes

`ConfigInput` replaces `NodeConfig` and calls each sublayer's `OnConfigChanged(old, new)`. The dispatcher checks that these calls emit nothing and enqueue nothing, as it does for the MAC's entry calls and `OnTimer` (section 3.5 rule 4). The rule for when a change takes effect: a value is read when it is used, never cached, except where a cached copy is part of protocol state. Concretely: CSMA timer values apply to the next timer started; `csma.mode` applies to the next contention computation; `dts.max_segment_size` applies to C_PDUs segmented after the change; `local_address` applies to D_PDUs encoded after the change (a change mid-link is an operator error, not something the core guards against); `groups` applies to the next D_PDU received. No change restarts a running timer or re-seeds the PRNG.

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

## 9. Work breakdown, phases 3 to 7

Three changes to the plan's phase contents, all forced by the T2 model and recorded here so the coordinator can update `docs/plan.md` section 12:

- **Annex K moves from phase 5 to phase 3.** Nothing can transmit without channel access, and the trace format already models it (`timer_expired csma.cont_wait` in the section 4.1 example). It is 111 rows with no PDUs; phase 5 keeps the Annex J analysis, the external busy input and the three-node simulation.
- **A thin SIS and CAS slice moves from phase 6 to phase 3, and CAS-1 link establishment from phase 5 to phase 4.** T2 inputs are modem, timer, config and SIS only (corpus-format.md 4.2). The only way a trace can give the node something to send is through the SIS, and ARQ needs `D_CONNECTION_MADE`, which only CAS-1 linking produces (C-0046 a priori knowledge does not apply). Adding an internal-primitive input to the trace format was considered and rejected: it would make the C++ port match an internal API, and the trace would stop being a statement about a real node.
- **Block alignment, ACK-ONLY fill and PADDING move from phase 3 to phase 4 (P4-3)**, because they need a peer known to be Ed4 (C-0560, C-0367) and only CAS-1 negotiation tells us that. "Address Not Known" stays with CAS-1 linking, now P4-1. WARNING generation for the unconnected states moves from phase 4 to P3-6, since the non-ARQ receive tables need it.

Each item is one PR, reviewed by a fresh Opus reviewer, with its corpus scenarios in a conformance PR merged first (test-first). Rows are cited by range; the PR author lists the exact ids in each scenario's `rows`. Scenario areas follow corpus-format.md section 1.

### Phase 3: runtime, thin SIS and CAS, CSMA, receiver, planner, non-ARQ DTS

Order matters because a T2 scenario can only observe what reaches an output. Received bytes have no observable effect until the MAC turns their EOTs into timer values or the SIS delivers them, and the MAC has nothing to contend for until the SIS can queue a C_PDU. So the thin SIS and CAS come first (their bind and validation behaviour is observable on its own), CSMA second (driven by data queued through the SIS, scenarios ending before LINKING), the receiver third (observed through the CSMA timers it changes), and the first `transmit` output arrives with the planner. Block alignment, ACK-ONLY fill and PADDING need a peer known to be Ed4 (C-0560, C-0367), which only CAS-1 negotiation establishes, so they move to phase 4.

| Item | Contents | Rows | T2 scenarios (area/name) |
|---|---|---|---|
| **P3-0** this design, then: `Node`, `CoreInput`/`CoreOutput`, dispatcher with the section 3.5 entry table and FIFO, `TimerTable` with the zero-duration rule, `TimerName`, `NodeConfig` and `ConfigReader`, `SeqSpace`, `FixtureModem`, `T2Runner`, `ScenarioBuilder`; a MAC stub that stays in SENSE. May split into runtime and runner PRs if review asks | corpus-format.md 4, harness-contract.md 2 and 3; the internal interface rows C-0025 to C-0038, B-0010, B-0011, B-0014, B-0016, B-0017; C-0786 (`SeqSpace`, unit tests) | K-0043, K-0063, K-0065 | `core/idle-sense` (burst_started, burst_ended, nothing queued, no outputs); `core/config-change` |
| **P3-1** thin SIS and CAS | connections, bind and unbind with reasons, SAP table, service type and DELIVERY MODE defaulting and override, invalid primitive counting and unbind reason 3, keep-alive, pre-bind handling, unidata request validation (MTU, TTL cap, group with ARQ, invalid delivery mode), DATA S_PDU with TTD, DATA C_PDU wrap and unwrap, non-ARQ requests accepted into the DTS queue (nothing transmitted yet: MAC stub), `groups`, `time.*`, `sis.default_ttl` | A-0030 to A-0058, A-0060 to A-0063, A-0065 to A-0093, A-0104, A-0106 to A-0111, A-0115, A-0116 to A-0125, A-0133 to A-0136, A-0142, A-0143 to A-0168, A-0210, A-0213, A-0233 to A-0236; B-0012, B-0013, B-0042, B-0112 to B-0116, B-0297, B-0298, B-0325; S-0009 to S-0016, S-0027, S-0039 to S-0045, S-0074 to S-0077, S-0085 to S-0100, S-0102, S-0119, S-0121, S-0137, S-0138, S-0140 to S-0149, S-0150, S-0157, S-0159 | `sis/bind-accept`, `sis/bind-sap-in-use`, `sis/bind-invalid-service-type`, `sis/unbind-rebind`, `sis/keep-alive`, `sis/invalid-primitives-unbind-3`, `sis/unidata-before-bind`, `sis/unidata-over-mtu`, `sis/unidata-group-arq-rejected`, `sis/delivery-mode-overrides-bind` |
| **P3-2** Annex K CSMA, DCD paths | the four visible states and Tables 1 to 5, DCD from `burst_started` and `burst_ended`, LBT rule 2, hold-while-busy, jitter, slotted and ed3 modes, own-last-transmission rule, queue-state sampling, the zero-duration chain, `csma.jitter` draw site registered, slot setting checks (K-0037, K-0040 as configuration validation); scenarios queue data through the SIS and end before LINKING | K-0005, K-0019, K-0037, K-0040, K-0059 to K-0066, K-0070, K-0074 to K-0092, K-0104, K-0107, K-0110; C-0840, C-0841, C-0845, D-0018 | `csma/sense-to-lbt-dcd-30s`, `csma/lbt-hold-while-busy`, `csma/jitter-draw` (seed 0 gives slot 15), `csma/slotted-position`, `csma/cont-wait-lost`, `csma/ed3-mode`. Three scenarios moved to P3-4, because each reaches LINKING or needs an own transmission, so its trace must contain a `transmit` output; until then the behaviour is covered by unit tests named in the matrix `tests` column. The six scenarios run against the real SIS, and the corpus summary shows no skips (the interim stand-in SIS was removed when P3-1 and P3-2 were integrated). |
| **P3-3** D_PDU stream receiver and VDCD | sync search over block boundaries, per-burst buffer reset, edition peek and decode, header CRC gate, type 13 resume rule, EOT to MAC (EOT 0 ignored), end-time combining (section 10 item 2), `csma.vdcd_end`, LBT rules 1 and 3 with two-node condition (a), heard-address record, SNR and lock bookkeeping, data CRC discard; observed through the CSMA timers | C-0973, C-0365, C-0829 to C-0838 (applicable), C-0839, C-0842 to C-0844, C-0868, C-0889, C-1022, D-0006, D-0007, D-0016, K-0020, K-0021, K-0067, K-0068, K-0071 to K-0073, K-0109 | `dts-rx/vdcd-lbt-3s`, `dts-rx/vdcd-end-after-burst-end`, `dts-rx/no-eot-uses-dcd`, `dts-rx/eot-zero-ignored`, `dts-rx/sync-search-junk`, `dts-rx/split-across-blocks`, `dts-rx/ext-type-resume`, `dts-rx/bad-header-crc`, `dts-rx/bad-data-crc-eot-still-used`, `csma/two-node-zero-lbt` |
| **P3-4** transmission planner, EOT and single-segment non-ARQ send | sections 5.1 to 5.7 for the classes phase 3 can fill: Expedited (type 8) and Data (type 7); Management and Warning arrive with P3-6 and NonArqRepeat with P3-5. Budget and the one-D_PDU exception (C-1042), layout with no block alignment, EOT and reception window stamping from the start of each D_PDU's block (C-1043), fixed waveform from `drc.default_speed`, `dts.max_segment_size`, `dts.max_transmission_ms`; type 7 and 8 single-segment send through the unconnected request tables, ARQ requests rejected to the CAS, type 7 held behind unplaced type 8 to the same peer (C-1044); C_PDU IDs from 0 (C-1040); first `transmit` outputs; the unbind ruling for U_PDUs held in the SIS (A-0260) | C-0103 to C-0106, C-0549 to C-0551, C-0555, C-0560, C-0848, C-0849, C-0851, C-0853, C-0854, C-0878, C-0880, C-0881, C-0888, C-0890 to C-0894, D-0003 to D-0005, D-0017, K-0093 to K-0095, K-0096; C-0570, C-0573 to C-0576, C-0590, C-0593 to C-0596, C-0610, C-0613 to C-0616, C-0630, C-0633 to C-0636; C-1040 to C-1044, A-0260 | `dts-tx/eot-single-block`, `dts-tx/eot-monotonic-three-blocks`, `dts-tx/no-align-group`, `dts-tx/budget-127500`, `dts-tx/oversize-dpdu-not-blocked`, `dts-nonarq/send-single-segment`, `csma/sense-to-transmit`, `csma/ed3-own-transmission` (the common LBT value after our own transmission, which tells ed3 from jitter), `sis/disconnect-queued-nonarq-still-sent`; from P3-2: `csma/jitter-slot-0-chain` (seed 6, whose first draw is 0), `csma/slotted-position-1-zero-wait`, `csma/slotted-own-last-tx` (K-0074, K-0082, K-0086, K-0090, K-0092). P3-4 also removed the test-only `MacSublayer.LinkingEntered` hook and the `NotSupportedException` on entering LINKING. The ARQ ordering rows C-0850, C-0852 and C-0855 are P4-2's, fill (C-0886) is P4-3's, and the D_CONNECTION rows of the unconnected tables are P4-1's |
| **P3-5** non-ARQ segmentation, repeats and TTL | segmentation of types 7 and 8 at build time (removing the P3-4 stopgap in `TransmitQueue.RequireSupported`, which throws on any build while a C_PDU larger than `dts.max_segment_size` or one with a non-zero retransmission count is queued, and filling the empty `TransmitQueue.ExpireDue`), C_PDU ID spaces and reuse, offsets, repeats within (spread) and across transmissions, reception window commitment and drop, TTL expiry drop, `dts.ttd`, queue emptied by expiry | C-0323 to C-0334, C-0338 to C-0357 (send side), C-0522 to C-0529, C-0541, C-0542, C-0857, C-0864, C-0882 to C-0885; K-0078, K-0091 | `dts-nonarq/send-three-segments`, `dts-nonarq/repeats-min-retx-2`, `dts-nonarq/repeats-across-transmissions`, `dts-nonarq/ttl-expired-dropped`, `dts-nonarq/group-address`, `csma/queue-emptied-to-sense`, `dts-nonarq/repeat-dropped-after-window` (C-0353), `dts-nonarq/repeats-two-cpdus` (C-1054 selection and rounds) and `dts-nonarq/segments-across-transmissions` (C-0880, C-1051). P3-5 removed both P3-4 stopgaps (`RequireSupported` and the empty `ExpireDue`) and registered `dts.ttd`, which every earlier scenario that queues data now shows; new interp rows C-1050 to C-1054. A TTL drop is rejected to the CAS, which passes `C_UNIDATA_REQUEST_REJECTED` to the SIS (nothing to answer until P6-1) |
| **P3-6** non-ARQ receive and unconnected-state behaviour | reassembly, reception window expiry, delivery in arrival order, DELIVER IN ORDER ignored, group delivery; WARNING generation and reasons, addressed-to-us scope, never warn on warning; MANAGEMENT type 6 receive, ack, frame IDs, `dts.management_wait`; UNRECOGNIZED-TYPE-ERROR EOW; `dts.reception_window`; EXTENSION and Ed4-only types only with Ed4 peers; the round trip through the SIS | C-0007 to C-0010, C-0336, C-0337, C-0362, C-0530 to C-0540, C-0562, C-0564 to C-0568, C-0376 to C-0395, C-0577 to C-0589, C-0597 to C-0609, C-0617 to C-0629, C-0637 to C-0649, C-0293 to C-0321, C-0983, C-1014 to C-1016, C-1020, C-0444 to C-0448, C-0730 to C-0735 | `dts-nonarq/rx-two-segments-out-of-order`, `dts-nonarq/rx-window-expires`, `dts-nonarq/rx-group-member`, `dts-nonarq/rx-not-addressed-ignored`, `dts-nonarq/warning-reason-1-arq-while-unconnected`, `dts-nonarq/warning-reason-4-extension`, `dts-nonarq/no-warning-on-warning`, `dts-nonarq/management-ack`, `dts-nonarq/management-repeat-same-id`, `dts-nonarq/eow-unrecognised-type-3`, `sis/nonarq-roundtrip` (request in, transmit out; bytes in, indication out). Departures, as delivered: WARNING reason 0 needs a peer recorded Ed3, which nothing records before P4-1, so P3-6 covers it by a unit test and `dts-nonarq/warning-reason-0-unknown-type` moves to P4-1; `dts.management_wait` is never started, because this node originates no MANAGEMENT message before phase 7 (DRC), which starts it and registers it; the J-0054 WARNING for the other unrecognised EOWs in a type 6 (7, 13, 14) is P5-3's (`mac/type6-unknown-subtype`) |
| **P3-7** T3 simulation harness | two or three `Node`s over a seeded lossy channel model in the tests project; broadcast to two receivers with loss; assertions on delivery and on no transmission overlapping an announced EOT | matrix `tests` column: C-0844, C-0889, A-0081 | none (T3) |

Phase 3 done when: every scenario above passes, the T3 broadcast test passes, and `docs/status.md` records it.

### Phase 4: CAS-1 linking and ARQ

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P4-1** CAS-1 link establishment and SIS soft link setup | Caller and Called procedures, request timer and retries, first-data timer, repeat request answered, Ed4 and Short negotiation, Ed3 fallback on WARNING, PeerTable edition record, `D_CONNECTION_MADE`, `link_state` registry (`unlinked`, `calling`, `linked`), soft link established on both sides, SIS queue until linked, `cas.max_links` reject reason 4, LINK bit reject reason 5, Address Not Known (B-0089, only when a complete station list is configured: NOT STATED where it comes from, so the PR either adds a setting with an interp row or records the row as not exercised) | B-0089 to B-0101, B-0118 to B-0152, B-0190 to B-0232 (applicable), B-0303 to B-0308, B-0313 to B-0315; A-0008, A-0010, A-0192 to A-0194; C-0043 to C-0056 (applicable), C-0571, C-0572, C-0591, C-0592, C-0611, C-0612, C-0631, C-0632 | `cas/make-caller-accepted`, `cas/make-called-accept`, `cas/make-called-too-many-links`, `cas/make-timeout-retry-then-fail`, `cas/make-repeat-request-answered`, `cas/ed3-peer-warning-fallback`, `cas/short-family-negotiated`, `cas/link-bit-rejected-5`; from P3-6: `dts-nonarq/warning-reason-0-unknown-type` (a type 9 to 14 from a peer recorded Ed3, C-0388). As delivered, also `cas/make-called-first-data-timeout` and `cas/make-caller-rejected`. Departures and readings: a CAS-1 C_PDU counts as sent when the transmission carrying it has ended, which the DTS reports with D_EXPEDITED_UNIDATA_REQUEST_CONFIRM (C-1100), so the Caller's request timer starts then and the Called node declares the link made then (B-0400); `cas.link_request_retries` counts repeats, 1 + retries sends (B-0407); links being made count towards `cas.max_links` (B-0402); in crossed calls a node that accepted the peer's request and then hears the peer's accept takes the values of its own request as answered, so both ends agree (B-0405, checked by the T3 `CrossedCallTests`); a WARNING starts the Ed3 fallback only if it names a type 8 D_PDU and our request has been sent (B-0411); D_CONNECTION_MADE carries the negotiated Ed4 and short-family flags; ARQ C_PDUs for a connected peer are held in the DTS with their TTD under `dts.ttd` and placed from P4-2, so ARQ scenarios end on the input that makes the link; CONNECTED-state ARQ D_PDUs are discarded until P4-2 and P4-6; Address Not Known is not exercised, since no station list is configured (B-0404); held U_PDUs of a failed make get reason 3 or 15 (A-0300); `UnconnectedPeers` is renamed `DtsPeers`. New interp rows B-0400 to B-0412, A-0300, C-1100 |
| **P4-2** ARQ data path, 16-bit family | windows and edges, FSN allocation, DATA-ONLY, ACK-ONLY bitmap, DATA-ACK, reassembly, in-order delivery flag, DROP, confirm and reject to CAS, CONNECTED-state tables for IDLE and DATA, failed D_PDU marking and retransmission, half-duplex failed rule, `dts.ack_wait`, `dts.max_window` | C-0502 to C-0517, C-0557, C-0558, C-0763 to C-0785, C-0787, C-0650 to C-0689, C-0850 to C-0866, C-0904 to C-0911, C-0170 to C-0193 and C-0195 to C-0250 (bitmap parts), C-0866, C-1000 to C-1007; C-0004, C-0013, C-0018 | `dts-arq/data-ack-roundtrip`, `dts-arq/window-full-waits`, `dts-arq/retransmit-after-bitmap`, `dts-arq/ack-wait-expiry-retransmit`, `dts-arq/deliver-in-order`, `dts-arq/drop-pdu-ttl`, `dts-arq/data-ack-merge`. As delivered, also `dts-arq/rx-data-acked`, `dts-arq/rx-drop-discards` and `dts-arq/ack-only-in-idle-warning-3`. Readings: `dts.ack_wait` with `dts.ack_wait_ms` (when null, derived from the transmission length plus the peer's longest turnaround) retransmits every unacknowledged D_PDU when no acknowledgement comes (C-1120); on each acknowledgement the D_PDU at RX LWE, those under clear bits and those after the last explicitly acknowledged one fail (C-1121); TX WIN flags are judged once the transmission's FSNs are allocated (C-1122); in-order delivery waits until RX LWE passes the C_PDU (C-1123); the DROP method also sends never-sent segments, and the receiver passes DROP PDUs with a bad data CRC (C-1124, C-1125); copies behind RX LWE are acknowledged (C-1126); a window-blocked C_PDU not yet started is passed over, one already started stops the data class (C-1127); one ACK-ONLY per peer per transmission, bitmap only, merged into DATA-ACK when it fits 18 bytes (C-1128); types 9 to 11 are processed in all four CONNECTED states ahead of P4-6 (C-1129); confirm when all segments are acknowledged (C-1130); ARQ data waits behind unplaced expedited data to the same peer (C-1131); retransmission order (C-1132); a DATA-ACK whose data CRC fails still brings its acknowledgement part (C-1133, from review, with `dts-arq/data-ack-bad-crc-still-acks`; also `dts-arq/ack-wait-derived`). ARQ C_PDUs on an 8-bit link stay held (P4-5) |
| **P4-3** acknowledgement encodings, fill and block alignment | type 10 long form with its CRC, run-length encoding, type 11 encoding bit, offset; ACK-ONLY repeats and PADDING in fill (section 5.5), block alignment and short non-final segments towards known Ed4 peers (section 5.4), none towards Ed3 or unknown | C-0195 to C-0235 (encoding parts), C-0237 to C-0250; C-0367 to C-0370, C-0552 to C-0554, C-0870 to C-0872, C-0882, C-0886, C-0887 | `dts-arq/ack-rle-long-run`, `dts-arq/ack-long-form-crc`, `dts-arq/ack-offset`, `dts-tx/block-align-ed4`, `dts-tx/no-align-ed3-peer`, `dts-tx/fill-ack-repeats`, `dts-tx/fill-padding-ed4`. As delivered: the acknowledgement takes the more compact encoding, the bitmap on a tie (C-1150), and is split at multiples of 256 FSNs into ACK-ONLY D_PDUs of at most 32 bytes of Ack Data each, all sent or none, when neither encoding fits 32 bytes (C-1151, coordinator ruling: keep ACK-ONLYs small, C-0213, p.C-31); it merges into a DATA-ACK only as one ACK-ONLY of at most 18 bytes (C-1152); receivers decode both encodings and apply the half duplex rule per ACK-ONLY (C-1153); fill as section 5.5 with the readings there (C-1154); alignment as section 5.4, ARQ only (C-1155). `no-align-ed3-peer` uses non-ARQ data, since ARQ to an Ed3 peer is P4-5's. Six P4-2 traces were re-derived for fill: `dts-arq/ack-only-in-idle-warning-3`, `ack-wait-derived`, `ack-wait-expiry-retransmit`, `data-ack-bad-crc-still-acks`, `data-ack-roundtrip` (PADDING after the DATA-ONLY or WARNING) and `data-ack-merge` (an ACK-ONLY copy after the DATA-ACK); no other trace changes, because no other transmission to a peer recorded Ed4 has room in its last block. New interp rows C-1150 to C-1155 |
| **P4-4** synchronisation and reset | Tests 1 to 5 on the arc model, FULL RESET procedure both roles, RESET/WIN-RESYNC receive handling, RESET FRAME ID | C-0253 to C-0274, C-0789 to C-0827 | `dts-arq/sync-test-1-fail-full-reset`, `dts-arq/full-reset-responder`, `dts-arq/full-reset-initiator`, `dts-arq/reset-frame-id-repeat-ignored`, `dts-arq/eq5-nothing-outstanding`. As delivered, also `dts-arq/sync-test-3-copy-not-acked` (issue #49 item c), `dts-arq/rx-win-cmnd-sets-rx-lwe` (the worked case that settles C-0274: the receiver of RESET RX WIN CMND sets its RX LWE) and `dts-arq/tx-win-rqst-answered`. New timer `dts.reset_wait/<peer>`; RESET D_PDUs are a control class after WARNING. New interp rows C-1170 to C-1180 |
| **P4-5** Ed3 family 0 to 3 and Short | the 8-bit family on a link negotiated Short or to an Ed3 peer, modulus 256, no PADDING and no Ed4 EOWs towards Ed3 | C-0052, C-0059 to C-0169 (family parts), C-0558, C-0730, C-0054, C-0367, B-0232 | `dts-arq/ed3-family-roundtrip`, `dts-arq/ed3-no-padding`, `dts-arq/short-family-window-128`. As delivered, also `dts-tx/no-align-ed3-peer-arq` (ARQ to an Ed3 peer keeps the maximum segment size and straddles a block, the case P4-3's review could not test) and `dts-arq/short-link-type9-warning-3`, plus an Ed3 variant of the T3 `ArqTransferTests` whose FSNs wrap modulo 256. `ArqLink` takes the family at D_CONNECTION_MADE (modulus, maximum window, initial edges 255 or 65535) and the planner and receive machine send and take types 0 to 3 on an 8-bit link. Readings: the 8-bit acknowledgement is one bitmap of the whole window, at most 16 bytes, so it is never split and always merges into a DATA-ACK (C-1190); PADDING, short segments and block alignment follow the peer's edition, not the family, so an Ed4 peer on a Short link gets them (C-1191); a regular ARQ D_PDU of the other family draws WARNING reason 3 (C-1192); `dts.max_window` is capped by the family's maximum window, 128 on an 8-bit link (C-1193). From review: received segments are numbered without wrapping, so a C_PDU of more segments than the window or the modulus is reassembled (C-1194, `dts-arq/ed3-long-cpdu-wraps`). No Ed4 EOW goes to an Ed3 peer because this node sends only EMPTY and UNRECOGNIZED-TYPE-ERROR, both all-editions EOWs (shown in `ed3-no-padding`) |
| **P4-6** expedited and management in CONNECTED states | type 4 receive and type 5 ack, EXPEDITED-DATA(CONNECTED) exits, MANAGEMENT(CONNECTED), queued requests released, allowed-type lists | C-0276 to C-0291, C-0755, C-0690 to C-0729, C-0736 to C-0738, C-0740 to C-0751, C-0761, C-1008 to C-1013; C-0005, C-0006, C-0015 | `dts-arq/expedited-rx-ack`, `dts-arq/management-connected-then-data`, `dts-arq/queued-request-released`. As delivered, also `dts-arq/management-connected-warning-3` (issue #45). Readings: the expedited receive window, one C_PDU at a time with FSNs from 0, type 5 built when placed (C-1210); type 5 is a control item after RESET, not repeated in fill (C-1211); EXPEDITED-DATA(CONNECTED) is left when no type 5 is owed and no type 4 C_PDU is partly received, for DATA(CONNECTED) if regular ARQ has flowed or requests are queued, else IDLE(CONNECTED) (C-1212); MANAGEMENT(CONNECTED) goes to EXPEDITED-DATA(CONNECTED) when a type 5 is still owed (C-1213); a reset out of EXPEDITED-DATA drops the owed type 5 (C-1214); the allowed-type lists are per-peer holds in the planner, released in the same transmission once the D_PDU ending the state is placed, which is also how queued requests are released (C-1215); an expedited ARQ DATA C_PDU stops `cas.first_data` (C-1216). New interp rows C-1210 to C-1216 |
| **P4-7** T3 ARQ | two nodes with loss, duplication and reordering; a long transfer completes; windows stay in sync | matrix `tests` column: C-0764, C-0790 | none (T3). As delivered, the T3 channel duplicates and reorders regular ARQ data D_PDUs within a transmission and adds late DATA-ONLY copies of earlier data (never late acknowledgements, which rightly fail Test 4), and `ArqTransferTests` and `ArqResetTests` gain variants using it, on both families. From issue #45 also `dts-arq/sync-test-2-fail-stays-idle`, `dts-arq/data-discarded-while-reset-pending`, `dts-arq/rx-win-cmnd-repeated` and `dts-arq/expedited-partial-abandoned`, and from review `dts-arq/eq5-rle-ack` (Test 5 on run length encoded acknowledgements). Readings: a D_PDU that fails the synchronisation tests makes no state change (C-1230); regular ARQ data in a later transmission than a partial type 4 C_PDU's last type 4 abandons it (C-1231, coordinator ruling) |

### Phase 5: link break, sessions, Annex J, three nodes

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P5-1** link break and clean close | Initiator and Responder, break timer and retries, type 5 to unknown peers, clean close with type 6 and its reasons, clean close timeout, `D_CONNECTION_LOST`, rejecting unprocessed C_PDUs, `link_state` `closing`, `breaking` | B-0154 to B-0174, B-0235 to B-0254, B-0275 to B-0289, B-0302, B-0309 to B-0312, B-0316 to B-0324; C-0652, C-0672, C-0692, C-0712, C-0739, C-0866 | `cas/break-initiator`, `cas/break-responder`, `cas/break-timeout-declared`, `cas/clean-close-no-data`, `cas/clean-close-higher-priority`, `cas/break-from-unknown-answered`, `cas/connection-lost-rejects-cpdus`. As delivered, also `cas/half-open-warning-1-broken` and `dts-arq/reset-never-acked-link-lost` (issue #49), and the T3 `HalfOpenLinkTests` (seed 349). Readings: unset CAS timers are derived from the answer wait (B-0420); the CAS asks the DTS for clean close with what may still start, and the DTS answers at once and reports clean state once, judged after each received burst, own transmission and `dts.ttd` (B-0421, B-0422); a received PHYSICAL LINK BREAK REQUEST closes the link from our end too (B-0423); `cas.break_retries` counts repeats (B-0424); CAS-1 C_PDUs met during a break (B-0425); two WARNINGs reason 1 break a half-open link, not while the first DATA C_PDU is awaited (B-0426); the DTS loses the connection at the sixteenth unanswered answer wait in a row, `dts.ack_wait` or `dts.reset_wait` (C-1250), sending D_CONNECTION_LOST before ending it (C-1251); an explicit `dts.ack_wait_ms` below `csma.lbt_wait_dcd_ms` is refused at load (C-1252). New interp rows B-0420 to B-0426, C-1250 to C-1252 |
| **P5-2** SIS soft link sessions | idle timeout, min hold on the called side, max session when other peers wait, break reasons and the rejections they cause | A-0011 to A-0015, A-0195 to A-0207, A-0200 | `sis-session/idle-break`, `sis-session/min-hold-called`, `sis-session/max-session-other-peer`, `sis-session/reject-reason-matches-break`. Carried over from P5-1: the SIS sends C_PHYSICAL_LINK_BREAK (nothing does yet, so our own clean close has unit tests only), and the rejections of C_PDUs held by a close carry the break reason (A-0207; the CAS passes no reason to the DTS yet). A C_PHYSICAL_LINK_BREAK that arrives while a close the peer asked for is under way is ignored by the CAS today: no stricter hold, no `cas.clean_close`; decide that here. As delivered: settings `sis.soft_link_idle_ms`, `sis.soft_link_min_hold_ms` and `sis.soft_link_max_ms`, null by default and then derived from the answer wait, three answer waits for idle and minimum hold and six for the maximum (A-0321); timers `sis.soft_link_idle`, `sis.soft_link_min_hold` and `sis.soft_link_max`. Readings: idle means no ARQ U_PDU outstanding and none received, both roles run it, and 0 closes at once (A-0320); the maximum session closes with Share Channel only while U_PDUs for another peer are held off for want of a link (A-0322); a held-off U_PDU of higher priority than everything outstanding on an open link closes that link with Higher Priority (A-0323); C_PDUs the DTS rejects when the connection ends after a SIS close carry its reason, and the SIS sends S_UNIDATA_REQUEST_REJECTED 9, 10 or 11 for those that asked for confirmation (A-0324; other reject reasons stay with P6-1); C_PHYSICAL_LINK_MADE carries the called role (A-0325); the minimum hold runs from link made and covers every unilateral close (A-0326); U_PDUs arriving during a close wait for a new link (A-0327); a SIS break during a peer's close joins it: new C_PDUs stop, `cas.clean_close` runs, no type 6 of ours (B-0450). New interp rows A-0320 to A-0327, B-0450 |
| **P5-3** Annex J analysis | WARNINGs for EXTENSION and WTRP D_PDUs addressed to us, type 6 unknown subtype (WARNING plus EOW type 3), reporting to the operator via S_MANAGEMENT_MSG_INDICATION | J-0001, J-0004, J-0009 to J-0011, J-0014, J-0015, J-0018, J-0020, J-0029 to J-0054, J-0056, J-0057, J-0059, J-0068 to J-0070, C-0735, C-0392 | `mac/extension-warning-reason-4`, `mac/wtrp-to-group-no-warning`, `mac/type6-unknown-subtype`. As delivered, also `mac/type6-token-eow-warning-3` and `mac/warning-rft-13-analysis`, and `dts-nonarq/warning-reason-4-extension` gains the report its WTRP EXTENSION draws. Departure: the channel analysis (`Runtime/ChannelAnalysis.cs`) sits on the DTS receive path, not in the MAC of section 4.2, because its reports must be enqueued as primitives and the MAC may not enqueue (section 3.5 rules 2 and 4); the MAC is unchanged. It reads every valid header, whoever it is addressed to, for reporting only, which widens section 10 item 9 (J-0083), and judges each received burst at its end. Readings: MANAGEMENT subtypes 7 and 14 draw WARNING reason 0 and 13 (the WTRP token) reason 3, only for new messages with VALID MESSAGE set, and EOW 13 is never answered with UNRECOGNIZED-TYPE-ERROR (J-0081); reports go to every bound client as S_MANAGEMENT_MSG_INDICATION, MSG TYPE 1, with a 9-byte body (finding code, node, WARNING addressee), only when a finding changes (J-0082); WTRP evidence is an EXTENSION of extended type 1 to 4 or EOW 13 (J-0083). New interp rows J-0081 to J-0083 |
| **P5-4** external busy input | `channel_busy` ORed into DCD, idle only when both clear (the only item that handles `channel_busy`; before it the MAC records the flag and ignores it) | K-0006, K-0048, K-0073, K-0100 | `csma/external-busy-holds-lbt` |
| **P5-5** three-node T3 | two links plus broadcast; no transmission overlaps an announced EOT; slotted and jitter both | matrix `tests` column: K-0089, K-0090, C-0889 | none (T3). As delivered, `ThreeNodeTests`: one node limited to one link so soft links are held off and closed idle, to share the channel and for higher priority, a second link between the other two, broadcasts from all, in-order and any-order ARQ, with checks on EOT overlap, delivery, the link limit, window consistency and the end state. Found issues #70 (stale ARQ state when a half-open link is made again), #71 (confirmed in-order C_PDUs lost at a link end), #72 (in-order C_PDUs reordered by priority) and #73 (no retry after a peer's reason 4); their signatures are counted as known and a strict variant is skipped until they are fixed |

### Phase 6: SIS completion and host

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P6-1** delivery confirmation | node confirm from `C_UNIDATA_REQUEST_CONFIRM`, client confirm with DATA DELIVERY CONFIRM and FAIL S_PDUs, confirm timers (collapsed), TTD on receive, frag size, reason mapping, over-MTU reject | A-0094 to A-0103, A-0105, A-0112 to A-0114, A-0169 to A-0185, A-0214 to A-0232, A-0237 to A-0243 | `sis-confirm/node-confirm`, `sis-confirm/client-confirm-roundtrip`, `sis-confirm/delivery-fail-sap-unbound`, `sis-confirm/timeout-reason-3`, `sis-confirm/frag-size-copy`, `sis-confirm/ttd-passed-discard`. Carried over from P5-1: client confirmations (DATA DELIVERY CONFIRM and FAIL S_PDUs) must be marked so that the clean close hold lets them start (B-0246, B-0318). As delivered, also `sis-confirm/receiver-sends-confirm`; settings `sis.frag_size` (null: a whole copy, issue #30) and `sis.confirm_wait_ms` (null: the U_PDU's TTD); timer `sis.confirm_wait`, the collapsed deadline list (no `sis.ttd`: a held U_PDU that asked for confirmation has its TTD in the same list, and one that did not is left to the DTS's TTD drop). Readings: CAS reject reasons map to 1, 9 to 11 or 3 (A-0340); one fragment size for every copy, the S_PDUs we send included (A-0341); the wait, its null value, the held U_PDU's TTD as reason 1, and the record ending at expiry (A-0342); peer answers matched by node, SAP IDs and copy prefix, oldest first, both answers in either order (A-0343); DATA DELIVERY CONFIRM and FAIL sent as the DATA came, with its priority and the default TTL, outstanding on the soft link and marked `ClientConfirmation` on C_UNIDATA_REQUEST and D_UNIDATA_REQUEST, which the clean close hold exempts (A-0344). The P5-5 three-node test client resends from the SIS rejections. New interp rows A-0340 to A-0344 |
| **P6-2** flow control and management | S_DATA_FLOW_ON and OFF policy, discard after FLOW_OFF, subnet availability, S_MANAGEMENT_MSG node information encoding, priority precedence across clients, shutdown unbind reason 6 | S-0019 to S-0026, S-0028 to S-0038, S-0046 to S-0053, A-0059, A-0064 | `sis/flow-off-discard-rejected`, `sis/management-node-info`, `sis/priority-precedence`. As delivered, also `sis/flow-off-reset-by-unbind` and `sis/flow-on-after-transmission`; interp rows S-0163 to S-0165 and C-1255. Two additions to this design: the DTS confirms normal non-ARQ requests (C-1255), so the SIS can count them for flow control; and shutdown is `Node.Shutdown(t)`, handled like an input but not a T2 input kind (section 10 item 1), so the C++ port needs its own test of it. The SIS reads the receive quality records through `CoreContext.RxQuality`, written only by the DTS (section 4.2) |
| **P6-3** host and client | the 5066 server in pdn-soundmodem (D3), `M0LTE.S5066.Client`, chat sample (Annex O); the host's same-millisecond class ordering (corpus-format.md 4.5) | S-0007 to S-0010 (host), client duty rows | none (host code, client duty tests) |
| **P6-4** T4 | two hosts over the audio simulation exchange chat both ways | plan 12 phase 6 done-when | none (T4). As delivered: `tools/s5066-test` (plan 11.6, D8, user doc docs/s5066-test.md), on `M0LTE.S5066.Client` only: chat (Annex O, SAP 5), file (ARQ unidata on data SAP 13 with node or client confirmation and the receiver's SHA-256 sent back) and perf (N messages of S bytes, confirmation and echo round-trip times, goodput), a line-mode UI, `serve`, and headless commands and JSON scenarios that write a JSON result with pass or fail. The monitor part is left to phase 8. `tests/M0LTE.S5066.T4.Tests` starts two `s5066d` processes on copies of the bench configurations (free ports) over UDP audio and runs one s5066-test scenario: chat a to b, chat b to a, both confirmed, and a 500-byte file with client confirmation whose hash matches at the far end; about 25 s. A longer variant (10 kB files both ways, perf with echo) runs only with `S5066_LONG_TESTS=1`. |

Twenty-four items. The scenarios named are the minimum; reviewers should ask for an edge case per interp row touched.

Coverage check (reviewer, against matrix at s5066-conformance cadd7f8): every applicable non-info row of A, B, C, D, J, K and S is now either cited above, cited by a T1 case, or named in a `tests` column, except these, which are deliberately left to later phases: the EOW rows C-0416 to C-0491 and the rate selection rows C-0918 to C-0960 (phase 7), C-1019 (phase 7, capability EOW), K-0007 (phase 8 station hooks, section 10 item 13), S-0005 and the encoding-rule rows S-0054 to S-0072, S-0118 to S-0136, B-0102 to B-0109 (T1 cases should cite them; a conformance PR, not core work), C-0020, C-0057, C-0058, C-0500, C-0559, A-0002, A-0006, A-0019, A-0022, A-0025, A-0028, B-0023, B-0039 (structural or host rows; the implementing PR cites whichever it touches). Re-run the check when the matrix changes.

### Phase 7: rate selection

Added 2026-11-08, after phase 6 closed. Plan 12 phase 7: EOWs 8 to 12 and 15 sent and acted on, ED3-BASIC-RATE to Ed3 peers, the algorithm of plan 6.2, calibrated by T4 sweeps over AWGN and Poor channels; done when a throughput versus SNR curve is published that beats a fixed-rate baseline at every SNR tested. The standard gives the EOWs and a few rules (plan 6.1) and leaves the algorithm to us [Ed4 C.7.6.2, p.C-120; C-0931, C-0932].

Two findings since plan 6.2 was written change how the algorithm is built:

- **The SNR estimate is post-equaliser** (M0LTE.Ms110d `docs/snr-calibration.md`, risk A5). On AWGN it reads the channel SNR. On fading it reads how hard the channel is for the waveform it was measured on, and above the waveform's mask SNR it hardly moves (WN 5 on Poor reads 3.6, 5.0 and 5.5 dB at 11, 17 and 23 dB set SNR). So plan 6.2's "highest WN whose D-LXIV figure plus a margin is below the smoothed SNR" does not work. Thresholds are calibrated as block error rate against the estimate, per waveform and interleaver, pairing one burst's estimate with the next burst's errors, and across waveforms (the estimate on the current waveform predicting errors on another). On fading, moving up cannot rest on the estimate alone: it needs a run of clean bursts and a trial at the higher rate.
- **Real radios cut the waveform.** Amateur SSB radios pass about 400 to 2600 Hz at -6 dB on transmit (FT-818 and FT-450 specifications), and the FT-818's receive filter is about 2.2 kHz wide at -6 dB (s5066-research `notes/bench-radios-ft450-ft818.md`), while 99% of the 110D signal's power lies between 400 and 3200 Hz (M0LTE.Ms110d `docs/waveforms.md`). Which waveforms survive the cut is not known until measured, and some may never decode at any SNR on such a path, while the estimate on a lower waveform still looks good. The selector must find a waveform that fails on a path and stop using it, without any SNR figure telling it so.

**Design shape.** Every phase 7 item follows these rules.

1. **Two halves that meet only on the air.** The receiver half (on the DTS receive path) keeps per-peer receive statistics and decides the recommendation EOWs it sends to each peer. The sender half (the planner's `ChooseWaveform`, section 5.3) picks each transmission's waveform and interleaver from the recommendations it has received and its own evidence. Each half is testable in T2 on its own.
2. **Deterministic, as section 6 asks.** Integers only (tenths of a dB, counts, parts per thousand). State changes only at named events: `burst_ended`, `transmit_done`, a `dts.ack_wait` expiry, a received EOW, and the link and connection primitives. No new timer name and no new PRNG draw site: probing runs on counters, not dice, and nothing expires by time. Smoothing is integer arithmetic with the sign tested before any division (section 6 rule 1).
3. **No MANAGEMENT D_PDUs.** Ed4 recommendations ride in unacknowledged EOWs (C-0099, C-0935), and the Ed3 stop and start procedure is not implemented (C-0922), so this node still originates no MANAGEMENT message and `dts.management_wait` stays unregistered (P3-6 departure).
4. **The ladder.** A rung is a (WN, interleaver) pair from WN 1 to 8 and 13 at Ultra Short, Short, Medium or Long (M0LTE.Ms110d 0.6.x has no WN 9 to 12; very short and very long are never sent, plan 5.3), at the fixed 3 kHz bandwidth (C-0933, plan 5.3). Rungs are ordered by the set (channel) SNR at which each meets its target in calibration, never by the estimate threshold, because on fading the estimate depends on the waveform it was measured on; the Poor channel orders the ladder, and calibration drops any rung that needs more set SNR than another rung of a higher data rate (open question 15). WN 0 is never on our ladder: it cannot be named in an EOW (plan 5.3), so we never recommend it, and we use it only to honour an Ed3 peer's 75 bps ceiling (open question 10).
5. **What T2 pins, and what calibration sets.**

   | Pinned by T2 scenarios (logic, in the core) | Set by calibration (data, `drc.table`) |
   |---|---|
   | Which EOW goes in which D_PDU header, and the encodings | Per-rung thresholds, in tenths of a dB of the estimate: for bulk, where the next rung's expected goodput (data rate x (1 - D_PDU error rate)) overtakes this rung's; for latency, where the D_PDU error rate falls below the stated latency target |
   | When recommendations are sent and to whom; Table C-25 prioritisation | Cross-waveform thresholds: the estimate on rung a at which rung b meets each criterion |
   | The smoothing arithmetic, the threshold comparisons and the ladder walk | The latency target (a D_PDU error rate, in parts per thousand), the smoothing weight, the clean-burst run before a trial, the failure limits |
   | Probing, ceilings and the step-down after silence, as counters | The ceiling lengths and their backoff cap |
   | Minimum over peers (C-0894), bulk or latency per transmission, MAX-SEGMENT cap | The ladder itself: which rungs, in what order |
   | The ED3-BASIC-RATE mapping table (C-0442), fixed in code with T1 cases | |

6. **Where the table lives.** `drc.table` is part of `NodeConfig`. The corpus `defaults.json` holds an empty table, meaning "no calibration": a node that does not know what its estimate means sends no speed recommendation, so a scenario that is not about rate selection never changes when the calibration does. Because the failure limits live in the table too, an empty table also disables the sender's own adaptation (no trial, no failed-trial ceiling, no step-down on silence): a node with an empty table transmits at `drc.default_speed` throughout, and announces Fixed = 0 all the same (C-1018). That is the fixed-rate baseline; there is no fixed-speed setting (open question 13). A `drc/` scenario supplies a small synthetic table and lists the rungs it reaches in `modem.waveforms`. The calibrated table is a data file in s5066-conformance (`calibration/<modem>-<version>/`, generated by a checked-in script from the sweep results) so the C++ port ships the same numbers. Calibration provenance: the directory names the modem implementation and version it was measured on (for example `ms110d-cs-0.6.1` for M0LTE.Ms110d); a node using another modem implementation, such as the C++ modem of ms110d-cpp, uses a calibration measured on that modem or one whose estimate is shown to match within the bin width, and the host refuses a table whose modem does not match the one it drives; the host loads it and overwrites `drc.table`, as it overwrites `time.*` (12.2). An operator does not set it.
7. **A waveform that never works on a path is caught by the sender.** A burst in which no D_PDU header decodes cannot be attributed to a sender on a shared channel, so a receiver never sees a total failure. The sender does: a transmission on a rung above the highest rung that last drew an answer from that peer, followed by a `dts.ack_wait` expiry with no answer, is a failed trial; the sender then caps that peer below the failed rung for a number of its transmissions to that peer, a number that doubles on each repeat failure up to a cap (table constants). The receiver adds a partial view: a burst on a rung whose D_PDU error rate exceeds the table's limit caps that rung in its recommendations, with the same backoff.
8. **Phase 7 churns traces once.** CAPABILITY replacing EMPTY towards known Ed4 peers (C-0426, P7-3) changes the EOW and header CRC bytes of most of the roughly 85 traces in `cas/`, `dts-arq/`, `sis-session/`, `sis-confirm/` and `mac/`, and of the `dts-tx/` scenarios that link with an Ed4 peer (`block-align-ed4`, `fill-padding-ed4`); the runner, not this list, decides which change. P7-3 re-derives them with `ScenarioBuilder`, and its reviewer checks that only EOW fields and CRCs moved. Recommendations do not churn them again, because the default table is empty.

| Item | Contents | Rows | T2 scenarios |
|---|---|---|---|
| **P7-1** modem 0.6.1 (issue #36) | In M0LTE.Ms110d: a burst cut short by NewPreamble drops its trailing noise-only probes from the SNR estimate, as SignalLost already does; a test; release 0.6.1 under its `docs/versioning.md` (additive) with the tag pinned to the merge commit SHA; bump this repo's package reference. The C++ follow-up goes on M0LTE/ms110d-cpp#98. Must be released before P7-5 measures anything, because calibration measures the estimator | none (modem) | none (modem test) |
| **P7-2** first-data wait, narrow form (issue #77) | Revise interp B-0403 per the coordinator ruling on #77: a complete DATA C_PDU from the Caller, received through ARQ and held by the DTS for in-order delivery (C-0178, C-1123), also ends the Called node's first-data wait. Only that; any other ARQ D_PDU from the Caller does not (not stated, ruled out). The DTS tells the CAS once per connection when such a C_PDU completes, through a primitive of ours or an extension of `DPeerArqHeard` (interp B-0452), whichever the author shows is smaller. Then check whether the T3 first-data departure (a wait longer than the run) can go. As delivered: a new primitive `DCpduHeld` (section 4.1), sent for each held C_PDU rather than once per connection, because only the CAS reads C_PDU types; extending `DPeerArqHeard` would have made a once-per-connection notice fire again. Two existing traces changed: in `dts-arq/deliver-in-order` and `dts-arq/in-order-held-delivered-when-link-ends` the first-data wait now stops when the held C_PDU completes, one D_PDU earlier. The T3 departure stays: without it 2 runs in 1200 still end with traffic in flight (3 before P7-2). New interp row B-0460 | B-0215, B-0216, B-0403, B-0452, C-0178, C-1123; interp range B-0460 to B-0464 | `cas/first-data-wait-ends-on-held-cpdu` (segments of an in-order C_PDU arrive after a gap; the wait is cancelled when the C_PDU is complete, before it is delivered), `cas/first-data-wait-not-ended-by-ack-only` |
| **P7-3** EOW scheduler, CAPABILITY, receive side | One EOW per D_PDU header, chosen per individual peer (open question 1). In each transmission the peer's headers start with an owed UNRECOGNIZED-TYPE-ERROR (C-1079, once), then the top-priority recommendation(s) of Table C-25, then SENDER-APPROACH when the transmission carries data to the peer, and then continue a per-peer rotation of the repeating set that persists across transmissions; CAPABILITY when the set is empty (C-0426; Ed4 bit 1, duplex 0). PADDING in fill takes the next entries of the rotation (C-0373, C-1154). D_PDUs to a group, a broadcast address or a peer of unknown edition carry EMPTY (C-0417, C-0419, C-0055, C-0937); a peer recorded Ed3 gets EMPTY or, from P7-6, ED3-BASIC-RATE, never an Ed4 EOW (C-0419, C-0938); a peer recorded Ed4 never gets ED3-BASIC-RATE (C-0417, C-0958). In every case an owed UNRECOGNIZED-TYPE-ERROR still goes first to an individual peer of any edition, as today (C-1079: an All-editions EOW). The repeating set is filled by P7-6 (recommendations) and P7-7 (SENDER-APPROACH); here it stays empty, so P7-3's visible change is CAPABILITY. Receive side: decode every EOW in a valid header of a D_PDU to our individual address and record, per peer, the latest MAX-SPEED, LOW-SPEED, MAX-SEGMENT, SENDER-APPROACH and ED3-BASIC-RATE; ignore recommendation EOWs in D_PDUs to a group or broadcast address (C-0937, open question 17), reserved speeds and interleaver 111 (C-0471), MAX-SEGMENT 0 (C-0476), SPEED-USED (C-0944; our modem reports the received waveform), and Ed4 EOWs from a peer recorded Ed3. A received CAPABILITY (open question 2): one with the Ed4 bit 1, in a valid header addressed to our individual address, records its source Ed4 only if no CAS-1 record exists for that address; a CAS-1 record (B-0408) always wins, and a later CAS-1 negotiation overwrites a CAPABILITY record; one with the Ed4 bit 0 is ignored. P7-3 amends the readings of C-0055 and MAIN-0009 (editions are discovered by CAS-1, or by a received CAPABILITY where CAS-1 has not spoken; sending is unchanged) and the PADDING condition of C-1154 (recorded Ed4 by either means), in the same PR, so the traces churn once. The records have no visible effect until P7-7, so they are unit tested here. Never send ED3-CAPABILITY (C-1019), SPEED-USED (C-0945) or a MANAGEMENT D_PDU with EOW 1 or 2 (C-0918) | C-0396, C-0415 to C-0426, C-0443 to C-0458, C-0462 to C-0471, C-0485 to C-0491, C-0918, C-0919 to C-0921 (refused since P3-6 under C-1071; P7-3 fills their `tests` column), C-0937, C-0938, C-0943 to C-0945, C-0958, C-1019, C-0045, C-0049, C-0055, C-1154, B-0408, MAIN-0008, MAIN-0009; interp range C-1260 to C-1269 | `drc/capability-replaces-empty-ed4`, `drc/empty-to-unknown-peer`, `drc/empty-to-ed3-peer`, `drc/owed-eow-before-capability`, `drc/padding-continues-rotation`, `drc/rx-capability-records-ed4` (unknown peer, then PADDING and CAPABILITY go to it), `drc/rx-capability-cas1-wins` (a peer recorded Ed3 by CAS-1 stays Ed3), `drc/rx-capability-ed4-bit-0-ignored`, `drc/top-priority-first-in-short-transmission`. Plus the trace re-derivation of design shape item 8 |
| **P7-4** channel simulator additions (M0LTE.HfChannel 0.2.0) | Additive, in M0LTE/M0LTE.HfChannel, so the modem sweeps and the stack sweeps share one channel: (a) a streaming form of `WattersonChannel` whose fading state, noise and filter state carry across calls, so bursts and the silence between them pass through one continuous fading process (needed to pair a burst with the next one at the real turnaround, and for the stack sweep, where bursts start whenever the protocol says); `Apply` today makes each call its own realisation. (b) A band-pass stage for an SSB radio path, defined by its -6 dB edges, default 400 to 2600 Hz (the research note's transmit figures), applied once for the sending radio and once for the receiving one; Tom's bench sweep of the real radios (the note's open items) replaces the default shape later. (c) The Moderate geometry used by the SNR sweep (two equal Rayleigh paths, 1 ms, 0.5 Hz) as a named preset. API lock test updated; consumers (pdn-soundmodem, the M0LTE.Ms110d tests) unaffected | none (test tooling) | none (unit tests: continuity across calls, the filter's measured -6 dB edges, Poor statistics unchanged) |
| **P7-5** modem calibration sweep | In the M0LTE.Ms110d tests project, beside the SNR sweeps and with the same environment-variable pattern: one continuous channel realisation per (channel, set SNR) carrying a deterministic schedule of bursts with a fixed gap between bursts (the turnaround of a 5066 exchange; P7-8 measures it, and until then 6 s), a known pseudo-random payload so every error is counted exactly, and the burst estimate of each burst. Each point's schedule holds the rungs whose error transition covers that set SNR plus their ladder neighbours, and is ordered so that every pair (a, b) the table needs (b within two rungs of a in ladder order, and a few further below) follows equally often; a plain cycle would give each rung only one successor. The burst length per rung is fixed in `meta.json`. Errors are counted per D_PDU, over the layout the planner builds on that rung (block aligned where a block holds a D_PDU, C-0872; straddling blocks where it does not, as on WN 1 Ultra Short, whose block holds 3 bytes), as well as per block; the D_PDU counts are what the table uses. From these, the bulk thresholds are where the next rung's expected goodput overtakes the current one's (no fixed error target; C.5.1.1 suggests around 50% FER maximises throughput [p.C-60]), and the latency thresholds are where the D_PDU error rate falls below the stated latency target ("very few" corrupted, C-0472), a number fixed in the table. Writes aggregated tables (calibration plan below), and a checked-in script turns them into the integer `drc.table` (the thresholds above, and the ladder ordered by set SNR on Poor) and a short results page beside `snr-calibration.md`. Run first on a small grid locally to measure speed, then on spot (the coordinator requests the instance) | plan 6.2; risk A5 | none (statistical) |
| **P7-6** receiver recommendations | Per peer and per rung (state that lives and dies with the peer's existing DtsPeers entry, section 6 rule 11): the smoothed estimate from `BurstEnded.SnrTenthDb` of each burst that carried a valid header from that peer (as `RxQuality` attributes bursts today, C-0833), the burst's D_PDU outcomes (valid headers, data CRC failures) and a clean-burst run counter. At each `burst_ended`: MAX-SPEED is the highest rung the table says meets the bulk target given the estimate on the rung just received, LOW-SPEED the same for the latency target; a move up is one rung at a time and only after the table's clean-burst run on the current rung (the trial); a move down is immediate; a rung whose burst D_PDU error rate exceeded the table's limit is capped with backoff (design shape item 7); with no estimate (`snr_tenth_db` null) the recommendation holds unless the error rate forces a step down. With an empty table no MAX-SPEED, LOW-SPEED, MAX-SEGMENT or ED3-BASIC-RATE is sent. Which of them fill the peer's repeating set follows the peer's last SENDER-APPROACH by Table C-25 (C-0950 to C-0954), mixed when none has come (C-0956). MAX-SEGMENT per open question 9. To a peer recorded Ed3, ED3-BASIC-RATE with Other Parameters 10 (C-0440), coded by the C-0442 table of open question 10. Recommendations to several peers in one transmission (C-0936), each only in D_PDUs to that peer (C-0937). P7-6 fixes the `drc.table` JSON schema and the exact integer smoothing formula in corpus-format.md section 6, with T1 vectors, so the C++ port reads the same table and computes the same values. As delivered (corpus 0.29.0): the schema is corpus-format.md 6.1 (keys `ladder`, `thresholds`, `smoothing_weight`, `clean_run`, `rx_error_limit_ppt`, `rx_ceiling_initial`, `rx_ceiling_cap`, and P7-7's `tx_step_down_waits`, `tx_ceiling_initial`, `tx_ceiling_cap`), and the arithmetic, step by step, is 6.2 (interp C-1270), with T1 kinds `drc_smooth` and `drc_ed3_basic_rate`. Choices the row left open, recorded in C-1270 to C-1273: the first recommendation is the target, not above the rung received when that rung is on the ladder; a burst on a rung with no `thresholds` entry gives no estimate; a cap counts down on every burst from the peer and a rung's failure count clears on a clean burst on that rung; MAX-SPEED or LOW-SPEED with no value yet is left out of the top priority and the set, so a peer heard only without an estimate gets MAX-SEGMENT alone; Fixed speed has no top priority; ED3-BASIC-RATE goes in every header to an Ed3 peer while a recommendation exists. Also delivers `drc/top-priority-first-in-short-transmission` (deferred by P7-3) | C-0427 to C-0442, C-0459 to C-0461, C-0472 to C-0476, C-0833, C-0935, C-0936, C-0939 to C-0942, C-0946, C-0947, C-0949 to C-0957; interp range C-1270 to C-1279 | `drc/rx-max-speed-after-burst`, `drc/rx-step-down-on-errors`, `drc/rx-trial-after-clean-run`, `drc/rx-rung-capped-after-failures` (and the doubled backoff), `drc/rx-no-snr-holds`, `drc/rx-empty-table-no-recommendation`, `drc/rx-approach-low-latency` (C-0952), `drc/rx-approach-fixed-segment-only` (C-0950), `drc/rx-default-approach-mixed` (C-0956), `drc/rx-ed3-basic-rate` (C-0440, C-0442, C-0957), `drc/rx-several-peers` (C-0936), `drc/rx-not-in-group-dpdu` (C-0937) |
| **P7-7** sender selection | `ChooseWaveform` (section 5.3): for each individual peer the transmission addresses, its rung is the received MAX-SPEED for a bulk transmission or LOW-SPEED otherwise (open question 7), mapped to a rung with exactly the recommended interleaver, or, when that rung is not on our ladder, the highest ladder rung whose data rate is not above the recommended one and whose interleaver is not shorter (open question 5; speeds compare by data rate, not WN number); for a peer recorded Ed3, its ED3-BASIC-RATE as a ceiling and interleaver (C-0959, C-0960, open question 10); with nothing received, `drc.default_speed`. Then the sender's own evidence: the failed-trial ceiling (design shape item 7) and a step down of one rung after the table's number of `dts.ack_wait` expiries in a row with no answer from the peer (plan 6.2; going lower never exceeds a recommendation, plan 5.3). Groups, broadcast and unknown peers use `drc.default_speed` (no recommendation can exist for them, C-0937; plan 6.2 non-ARQ). The transmission takes the lowest rung over its addressees (C-0894, C-0930). A recommendation lasts until replaced or until the peer's connection ends (open question 4); received recommendations and ceilings are kept in the peer's DtsPeers entry and go with it. MAX-SEGMENT caps the new segments of C_PDUs to that peer at value x 4 bytes, fixed for the transmission (C-0476, C-0549). SENDER-APPROACH (open question 8) enters the peer's repeating set at the head of each transmission that carries data to it. No fixed-speed setting (open question 13): SENDER-APPROACH always announces Fixed = 0 (C-1018), and with an empty `drc.table` every transmission stays at `drc.default_speed`, which is the fixed-rate baseline (design shape item 6). The node information message shows the speed in use per peer (P6-2, interp S-0164; the placeholder in `SisSublayer`). Segments built for a faster rung keep their size when retransmitted slower (C-0549 notes) and may straddle blocks; allowed, and counted in the sweeps | C-0477 to C-0484, C-0461, C-0503, C-0523, C-0894, C-0926, C-0927, C-0929 to C-0932, C-0441, C-0471, C-0476, C-0948, C-0959, C-0960, C-1018; interp range C-1280 to C-1289 | `drc/tx-default-until-recommendation`, `drc/tx-follows-max-speed`, `drc/tx-low-speed-for-short-transmission`, `drc/tx-min-over-peers` (C-0894), `drc/tx-group-at-default`, `drc/tx-step-down-on-silence`, `drc/tx-failed-trial-ceiling` (and its doubling), `drc/tx-max-segment-caps-segments` (C-0476), `drc/tx-reserved-speed-ignored` (C-0471), `drc/tx-off-ladder-recommendation` (open question 5), `drc/tx-ed3-basic-rate-ceiling` (C-0959, C-0960), `drc/tx-sender-approach`, `drc/tx-empty-table-fixed` (an empty table keeps `drc.default_speed` through silence and recommendations-free exchanges), `drc/tx-recommendation-cleared-at-link-end`, `sis/management-node-info-speed` |
| **P7-8** stack sweep harness | `tools/s5066-sweep`, C# only (T4 is statistical, not byte-exact): two `Node`s, each with an in-process, synchronous MS110D modem wrapper (modulate and demodulate in 20 ms chunks, keeping the rules of 12.5), joined by the P7-4 channel in each direction (continuous Watterson, optional SSB band-pass, AWGN at the set SNR), on a virtual clock that advances in 20 ms steps and orders inputs as 12.3 and 12.4 do, so a run goes as fast as the demodulator and repeats exactly for a seed. A small in-process SIS client drives a bulk ARQ transfer one way, with chat-sized messages the other way. One JSON result per run: configuration, seed, channel, set SNR, delivered bytes and goodput over the measured window, transmissions per rung and their outcomes, retransmission rate, ACK-only transmissions, links lost, and the EOT overshoot the receiver saw (issue #66). One real-time cross-check: the P6-4 T4 setup (two `s5066d` processes over UDP audio) with the P7-4 channel inserted in the audio path, at two points, to show virtual and real time agree and to measure host latency (issue #38) | plan 12 phase 7; issues #38, #66 | none (T4) |
| **P7-9** calibrated table, tuning, residual decisions | The host loads the calibrated table (design shape item 6). Stack sweeps of the adaptive node in rounds (calibration plan), tuning only `drc.table` constants between rounds. Then decide, each from P7-8 evidence, as "no change" with the numbers or as a small core change with its T2 scenario and interp row: issue #66 (the EOT overshoot is about one block of the received waveform, so up to 10.24 s on WN 1 and 2 Long: whether the host should warn when `lbt_wait_vdcd_ms` is shorter than the longest block on the ladder, as it warns for K-0048, and what the bench and T3 values should be; the prediction is never shortened, C-0844); issue #67 (with P7-7 an ACK-only transmission goes at the LOW-SPEED rung, a short block; whether such a transmission should still take a second block depends on the links lost at the Poor points); issue #38 (whether C-1051 needs a host latency term, from the measured latency). The failed-trial ceiling lengths are tuned so that failed trials, each costing one `dts.ack_wait`, stay well inside the 16 answer waits after which C-1250 declares the connection lost: no sweep point may lose a link through trials alone | C-0842 to C-0844, K-0074, C-1051, C-1052, C-1054, C-1154, C-1250, K-0048, C-0894; interp ranges C-1290 to C-1299, K-0121 to K-0125 | as each decision needs |
| **P7-10** published curve | Final sweeps (calibration plan); `docs/rate-selection.md`: method, the table version and modem version, the throughput versus SNR curve per channel with the baseline (open question 14), a TSV of every point and an image; the done-when checked point by point; plan 12 and `docs/status.md` updated | plan 12 phase 7 done-when | none |

**Order and parallel work.**

1. Start together, since they touch different repositories or different parts of the core: P7-1 (M0LTE.Ms110d), P7-4 (M0LTE.HfChannel), P7-2 (CAS) and P7-3 (planner and receiver). P7-2 and P7-3 reserve separate interp ranges (above); whichever merges second does the small integration.
2. After P7-1 and P7-4: P7-5. A small local run measures the speed, then the coordinator requests the spot instance; the full run goes in the background while the core work continues.
3. After P7-3: P7-6 and P7-7 in parallel, against synthetic tables, so neither waits for calibration data. They share only the per-peer EOW records P7-3 defines.
4. After P7-4: P7-8, in parallel with P7-6 and P7-7. The fixed-rate baselines can run on today's core by setting `drc.default_speed`, so they need not wait for P7-7.
5. After P7-5 to P7-8: P7-9, then P7-10.

The critical path is P7-3, then P7-6 and P7-7, then P7-9 and P7-10. P7-4 and P7-5 run beside it.

**Calibration plan.**

Measured speed so far: by our count the 0.6.0 SNR sweep demodulated about 10 hours of signal in 27 minutes on a 16-core box with 6 to 8 workers, so 0.35 to 0.7 core-hours per signal-hour. P7-5 re-measures this on its small grid before the request. Heavy runs go on AWS spot instances requested by the coordinator, never on GitHub-hosted runners; only the aggregated tables come back.

| Sweep | Grid | Size | Compute (rough) |
|---|---|---|---|
| Modem block error rate (P7-5) | 9 waveforms x 4 interleavers; AWGN, Poor, Moderate, AWGN with SSB band-pass, Poor with SSB band-pass; set SNR in 1.5 dB steps from where block errors are near 100% to where they vanish (about 12 points) | At least 300 blocks per rung and point, and at least 20 minutes of channel per fading point (Long blocks are 7 to 10 s, so about 50 minutes there); about 850 signal-hours | About 600 core-hours: 3 to 4 hours of one 192-core instance |
| Stack baselines (P7-8 harness, for P7-9) | AWGN, Poor, Poor with SSB band-pass; set SNR 0 to 30 dB in 3 dB steps; fixed rungs WN 1 to 8 and 13 at the Long interleaver (the bulk interleaver, plan 6.2); 3 seeds; 30 minutes of channel each, demodulated by both nodes | About 900 signal-hours | About 700 core-hours: about 4 hours of a 192-core instance; run once |
| Stack adaptive, per tuning round (P7-9) | The same channels and points, the adaptive node, 3 seeds | About 100 signal-hours | About 80 core-hours: 30 minutes on spot, or most of a day on our own 12-core box |
| Final (P7-10) | Adaptive and the chosen baseline, 5 seeds | About 300 signal-hours | About 200 core-hours |

Stack sweeps count both nodes' demodulators, which run for the whole channel time. All told about 1,800 core-hours, about 10 hours of one 192-core spot instance over three sessions (calibration, baselines with the first tuning rounds, final).

Output formats:

- P7-5, `calibration/ms110d-<modem version>/` in s5066-conformance, tab-separated: `own.tsv` with channel, rung, set SNR (tenths of a dB), estimate bin (5 tenths wide), bursts, blocks, block errors, D_PDUs and D_PDU errors; `next.tsv` with channel, rung a, rung b, gap in ms, bin of the estimate on a, and the D_PDUs, D_PDU errors, blocks and block errors of the following burst on b; `meta.json` with the modem implementation, version and commit, HfChannel version, the SSB filter edges, the channel and payload seeds, the burst length per rung, each point's schedule and the grid. The derived `drc-table.json` (integers only) comes from the checked-in script, never by hand. Raw per-burst records stay on the instance until the summary is checked, then are deleted, apart from a 1% sample kept for audit.
- P7-8 and later: one JSON result per run, and a `curve.tsv` per channel (set SNR, configuration, seeds, goodput mean, minimum and maximum in bit/s, retransmission %, transmissions per rung, links lost).

**Open questions where the annex leaves a choice.** All seventeen were ruled by the coordinator on 2026-11-08, on the review of PR #91. Each implementing item records the reading as an interp row in its range. Questions 13 and 14 are plan choices, not annex gaps.

| # | Question | Reading | Rows |
|---|---|---|---|
| 1 | A header holds one EOW. When several are wanted for a peer, which goes where? Table C-25 gives priorities but no placement | Ruled 2026-11-08: Per peer, in each transmission: an owed UNRECOGNIZED-TYPE-ERROR first, then the top-priority recommendation(s) of Table C-25 (priority decides use when slots are limited [p.C-122]), then SENDER-APPROACH when the transmission carries data to the peer, then a rotation that persists across transmissions through the repeating set in a fixed order per approach (bulk: MAX-SPEED, MAX-SPEED, MAX-SEGMENT; low latency: LOW-SPEED, MAX-SEGMENT; mixed: MAX-SPEED, LOW-SPEED, MAX-SEGMENT; intermediate: MAX-SPEED, LOW-SPEED, MAX-SPEED, MAX-SEGMENT; fixed: MAX-SEGMENT); CAPABILITY when nothing else is wanted. The top priority recurs, as C-0947 allows; PADDING continues the rotation | C-0947, C-0949, C-1079, C-1154; P7-3 |
| 2 | A CAPABILITY arrives from a peer not recorded Ed4. C.3 offers it for discovery (C-0049) but our C-0055 reading discovers editions by CAS-1 only | Ruled 2026-11-08: A CAPABILITY with the Ed4 bit 1, in a valid header addressed to our individual address, records the source Ed4 only if no CAS-1 record exists for it; a CAS-1 record (B-0408) always wins and a later CAS-1 negotiation overwrites a CAPABILITY record; the Ed4 bit 0 is ignored. Receiving breaks no shall (the Table C-4 notes limit sending, C-0419), and C.3 offers the EOW for this (C-0045, C-0049); CAS-1 wins because a pre-Ed4 node may use type 15 for something else (see the note), and an upgraded peer negotiates Ed4 at its next link (C-0047). C-0055's limit stays on sending. Amends C-0055, MAIN-0009 and the PADDING condition of C-1154, in P7-3 | C-0045, C-0049, C-0055, C-0419, MAIN-0008, MAIN-0009, B-0408, C-1154 (its PADDING condition says recorded Ed4 by CAS-1); P7-3. Note: Ed1 Annex C Table C-4 lists EOW types 7 to 15 as unspecified or user-defined; the Ed3 text is not available locally, so whether a pre-Ed4 node can send a type 15 of another meaning is NOT STATED here |
| 3 | When, and to which peers, does a receiver send recommendations? "A node receiving data" (C-0459) | Ruled 2026-11-08: To every peer recorded Ed4 (or Ed3, ED3-BASIC-RATE) from which we have received a burst with a valid header, whenever the table is not empty; the latest values repeat in the rotation of every D_PDU to that peer until they change. No expiry by time | C-0459, C-0472, C-0936, C-0937; P7-6 |
| 4 | How long does a received recommendation stand? | Ruled 2026-11-08: Until replaced, or until the peer's connection ends (link broken or connection lost); the next link starts again at `drc.default_speed`. For a peer we never link with (non-ARQ unicast), until replaced | NOT STATED; P7-7 |
| 5 | MAX-SPEED and LOW-SPEED give the shortest interleaver recommended; the sender may choose longer (C-0461, C-0473). And interleaver 000 means no recommendation | Ruled 2026-11-08: Use exactly the recommended interleaver: the receiver measured that rung. For 000, the interleaver of `drc.default_speed`. A recommended rung not on our ladder maps to the highest ladder rung whose data rate is not above the recommended one and whose interleaver is not shorter; speeds compare by data rate, not WN number (WN 13 is 2400 bps) | C-0461, C-0469, C-0473, C-0894; P7-7 |
| 6 | Several addressees: C-0894 bounds the speed only | Ruled 2026-11-08: The lowest rung over the addressees by ladder order, so interleaver and speed come from one measured rung; groups and unknown peers count as `drc.default_speed` | C-0894, C-0930; P7-7 |
| 7 | Throughput or latency for this transmission? C-0929 item 5 says it may be inferred | Ruled 2026-11-08: Bulk (MAX-SPEED) when the data D_PDUs the transmission would carry to the peer, new or retransmitted, ARQ or not, need more than one block at the MAX-SPEED rung; otherwise latency (LOW-SPEED): acknowledgements, control and short messages. Worked out from the queue before the build, so deterministic | C-0929, C-0931; P7-7 |
| 8 | Our SENDER-APPROACH: C-0484 expects service configuration, and we have none | Ruled 2026-11-08: Variable speed, strategy mixed (10), because we choose between both recommendations per transmission and want both; EOW 12 Needed 0, since our modem reports the received waveform (C-0481, C-0482); Fixed always 0 (C-1018, open question 13) | C-0477 to C-0484, C-0948; P7-7 |
| 9 | The MAX-SEGMENT value a receiver sends; Table C-25 has it at every approach, but nothing says how to compute it | Ruled 2026-11-08: To a variable-speed sender, 255 (1020 bytes): speed is the lever, and block alignment already sizes Ed4 segments to the blocks (C-0872). To a fixed-speed sender, start at 255, halve after a burst with a D_PDU error rate over 50% and double after a run of clean bursts, within 25 (100 bytes) and 255, after the guidance of [Ed4 C.5.1.1, pp.C-60 to C-61] | C-0474 to C-0476, C-0950 to C-0954; P7-6 |
| 10 | ED3-BASIC-RATE codes against 110D waveforms; C-0442 says define our own table | Ruled 2026-11-08: Sending: the highest Table C-5 rate not above the rung's data rate (WN 1 150, WN 2 300, WN 3 600, WN 4 and WN 5 1200, WN 13 2400, WN 6 3200, WN 7 4800, WN 8 6400 bps); Ultra Short and Short as short (01), Medium and Long as long (10). Receiving: the fastest waveform whose rate is not above the code (75 bps gives WN 0), short as Short, long as Long, no interleaving as Ultra Short, reserved (11) ignored as C-0471 | C-0431 to C-0442, C-0959, C-0960; P7-6, P7-7 |
| 11 | WN 0 cannot be named in an EOW (plan 5.3) | Ruled 2026-11-08: Never on our ladder and never recommended; used only to honour an Ed3 peer's 75 bps ceiling | C-0463, C-0470; P7-6, P7-7 |
| 12 | The step-down count, clean-burst run and ceiling lengths: settings or not? Plan 6.2 says "a configured number", plan 3.2 principle 5 says no settings nobody asked for | Ruled 2026-11-08: Not settings: constants in `drc.table`, so tuning changes data, not code, and T2 pins the logic with a fixture table | plan 3.2, 6.2; P7-6, P7-7 |
| 13 | A fixed-speed mode. The Fixed bit of SENDER-APPROACH (C-0479) needs a meaning, fixed speed is allowed (C-0926), and the done-when needs a baseline | Ruled 2026-11-08: No setting. An empty `drc.table` on both nodes plus `drc.default_speed` is the fixed-rate baseline, set by the harness; C-0479 binds only a node that sends Fixed = 1, and C-1018 lets us always send Fixed = 0 | C-0479, C-0926, C-1018; P7-7 |
| 14 | Which fixed-rate baseline must the curve beat at every SNR? | Ruled 2026-11-08: Per channel, the single fixed rung (WN 1 to 8 or 13 at the Long interleaver, the bulk interleaver of plan 6.2) with the highest throughput summed over the sweep: the best an operator could pick without knowing the channel. The adaptive node must beat it at every SNR on AWGN and Poor at which any configuration delivers data, where beat means a mean goodput over the seeds strictly above the baseline's mean at that point (minimum and maximum reported). Each point's best fixed rung (an oracle) is reported with the gap, not gated. On Poor with the SSB band-pass, gated on no link lost and under 5% of transmissions on rungs that never decode there after the first ceiling | plan 12; P7-10 |
| 15 | Which rungs make the ladder? | Ruled 2026-11-08: Measure all 36; keep, separately for bulk and latency, the rungs that no other rung beats on both data rate and the set SNR at which it meets its criterion (not the estimate threshold, which is waveform-dependent on fading); the ladder is ordered by the Poor channel | plan 6.2; P7-5 |
| 16 | A failed trial on the sender side: what counts as no answer? | Ruled 2026-11-08: A `dts.ack_wait` expiry for the peer with nothing heard from it since the transmission (the answer of C-1250); a non-ARQ unicast transmission draws no answer, so it never counts as a trial | C-1120, C-1250; P7-7 |
| 17 | Recommendation EOWs in a D_PDU to a group or broadcast address (C-0937 says senders never do it) | Ruled 2026-11-08: Ignore them | C-0937; P7-3 |

Phase 7 done when: every scenario above passes; P7-10's curve beats the baseline of open question 14 at every SNR tested on AWGN and Poor; the SSB band-pass gate of open question 14 holds; `docs/rate-selection.md` is published; and `docs/status.md` records it.

Phase 7 coverage: the EOW rows C-0416 to C-0491, the rate selection rows C-0918 to C-0960 and C-1019 are each cited by an item above, except the rows the matrix marks as not applying (C-0400, C-0403, C-0404, C-0411, C-0414, C-0457, C-0458, C-0922 to C-0925, C-0928, C-0934), which need no test, and the info rows C-0926 and C-0933, cited for context. The reviewer of P7-10 re-runs the coverage check of this section against the matrix of that day.

---

## 10. Choices the standard does not make (NOT STATED)

Each is an implementation choice a later item will need. Phase 7's choices are listed with its work breakdown (section 9, phase 7, open questions). Where a matrix row exists it is named; where none does, the implementing PR adds an `interp` row to the matrix in the conformance repository with the reading below, unless its reviewer finds a better one.

| # | Choice | Reading proposed here | Row |
|---|---|---|---|
| 1 | START and STOP of Annex K come from "subnetwork management", undefined | Construction is START (SENSE at time 0). STOP is the host destroying the core. No trace input. P6-2: just before, the host calls `Node.Shutdown(t)`, which sends unbind reason 6 to every bound client and closes every connection (interp A-0059); the MAC is not told. | K-0058, K-0062 (notes) |
| 2 | How a receiver turns a parsed EOT into an end time, and how several are combined | Each valid header with EOT > 0 gives `t_rx + eot * 500`, where `t_rx` is the `t` of the `bytes_received` that completed the header (EOT 0 gives nothing, C-0105). Each is an upper bound on the true end (EOT rounds up and the bytes arrive after the D_PDU started), so the estimate of a burst is the minimum over its headers, started afresh at `burst_started`; an earlier burst's estimate is carried and the later of the two binds (C-0844), and rule 1 does not apply when the carried one produced the idle (K-0072). It overstates the end by at least the receive latency of one block; that costs turnaround, never safety, and T4 measures it | C-0842, C-0843, K-0021 (new interp) |
| 3 | How the MAC learns that a predicted EOT end time has passed, when no input arrives then | `burst_ended`, whatever its reason, never clears the prediction: the node may not transmit before the end time even if it hears no signal [Ed4 C.7.5.1.1, p.C-110; C-0844]. The MAC runs `csma.vdcd_end` to the predicted end time (section 4.3), and its expiry is the VDCD idle event. A tie counts as VDCD (K-0073) | K-0073 (extend), C-0844 |
| 4 | The EOT of a D_PDU inside an interleaved block | Air start of a D_PDU = start of the block holding its first byte, `P + b_i * B`; never early, so EOT is never early | C-0104, C-0891; recorded as C-1043 (P3-4) |
| 5 | Reception window when repeats span later transmissions | Place all repeats in one transmission when they fit (spread, C-0885). If not, window = this transmission's remaining time plus, per further transmission needed, `max_transmission_ms + lbt_wait_vdcd_ms + num_cont_slots * cont_slot_width_ms`. This is not a bound on when our next turn comes (other nodes may hold the channel), so it is a commitment the sender keeps by dropping repeats that would fall outside it (C-0353, section 5.6); T3 measures how often that happens (P3-7, with one sender: never at 0 to 300 ms of host latency in jitter, ed3, or slotted below the last position, which always leave a slot of the allowance unused; slotted at the last position has no slack beyond the whole-block remainder and the half-second rounding, so any latency can drop the last repeat of a U_PDU; see `SimulationTests.Reception_window_commitment_under_host_latency`). P3-5 made the number of further transmissions exact (the owed copies packed in order into transmissions of the configured length) and the LBT term the value used after an own transmission (`lbt_wait_dcd_ms` in ed3 mode) | C-0350, C-0352, C-0353, C-0884; recorded as C-1051 and C-1052 (P3-5) |
| 6 | When to retransmit if no acknowledgement ever arrives | `dts.ack_wait/<peer>` started at `transmit_done`, value `dts.ack_wait_ms`, or when that is null the transmission length plus the longer LBT wait plus the longest contention wait; expiry marks every unacknowledged D_PDU to that peer failed (the C-0911 "retransmit" branch) | C-0904, C-0911; recorded as C-1120 (P4-2); plan A4 |
| 7 | Fill order of a transmission across classes | Section 5.3 order: management, warning, CAS control, expedited, acks, retransmissions, new data, non-ARQ repeats, then fill | C-0855, C-0297, C-0288, B-0280; recorded as C-1041 (P3-4) |
| 8 | Decoding types 9 to 14 from a peer whose edition is unknown | Decode as Ed4 (`peer_edition` ed4). Only a peer recorded Ed3 gets the Ed3 reading, where types 9 to 14 draw WARNING reason 0 | C-0051, C-0388 (new interp) |
| 9 | What the MAC may learn from D_PDUs addressed to others | Only the EOT and the (src, dst, group) triple for the channel model and the two-node rule; no state machine runs for them. P5-3: the Annex J channel analysis also reads their type, extended type, EOW type and WARNING fields, to report other nodes' MAC modes to the operator, and nothing else | C-0868, K-0072; J-0083 (P5-3) |
| 10 | Request identity across the internal primitive interfaces | An integer handle allocated by the dispatcher; never on the wire | C-0039, B-0018 (notes) |
| 11 | Timer values for CAS linking and breaking, confirmation waits, soft link times | Configurable with no default (`null` in `defaults.json`); scenarios supply them; T4 derives recommended values (plan A4). P5-1 (issue #49): the CAS timers, when null, are derived from the answer wait of C-1120 (B-0420); P5-2: so are the soft link times (A-0321) | B-0203, B-0283, A-0221, A-0226, A-0197 |
| 12 | Where TTD wall-clock comes from in a core with no clock | `time.epoch_year`, `time.epoch_day` and `time.epoch_second` in configuration; the host sets them at start-up | A-0161, A-0162, A-0163, A-0166, A-0167 (new interp) |
| 13 | Station-layer identification hook transmissions (D4, K-0007) | Not in phases 3 to 6. Phase 8 will need an input (hook requests a turn, with its duration) and an output (turn granted) so the hook obeys Annex K from inside the core. Both are corpus-format additions for that phase | K-0007 |
| 14 | `terminate_receive` and `set_max_input_blocks` | Never emitted in phases 3 to 6 | plan 5.1 |
| 15 | Merging an ACK into a DATA D_PDU | When a peer receives both an acknowledgement and new data in one transmission, the first data D_PDU to it becomes DATA-ACK and the ACK-ONLY is dropped; ACK-ONLY repeats in fill still refer to the same RX LWE | C-0658, C-0678 ("as appropriate"); recorded as C-1128 (P4-2), refined by C-1152 and C-1154 (P4-3) |
| 16 | The address size m a sender uses | The smallest whole number of nibbles that holds both addresses, chosen per D_PDU; no setting (section 2.3) | C-0133 |
| 17 | The LBT_WAIT_TIMER value when the channel is idle again after a hold in LBT_WAIT or CONT_WAIT | K.3.2.3 and K.3.2.4 say the DCD value; taken as written, a node that lost one round needs that much silence, which a sender with more data than its window never gives, and T3 lost live ARQ links in every seed (issue #64). The held timer takes the value the LBT rules give for that idle, rules 1 to 3 as on entry from SENSE; the DCD value is the one in force while the end of the busy period is unknown. A recorded departure from the letter of K-0077 and K-0089; the timer still starts only once the channel is idle (C-0844) | K-0077, K-0087, K-0089; recorded as K-0120 (issue #64) |

---

## 11. Proposed changes to the corpus format and harness contract

To be made in the conformance repository (minor version bump before 1.0.0) alongside P3-0.

1. **corpus-format.md section 5, and harness-contract.md section 2 item 2.** State that the `PlanBurst` answer exposes `preamble_ms`, `block_ms`, `flush_ms`, `bytes_per_block`, `blocks` and `duration_ms`. Reason: the core needs the per-block start times to stamp a different EOT on each D_PDU (section 5.6) and the block capacity to size a transmission (section 5.3); today the text gives only the derived duration. The fixture already holds every value, so no file changes. Also say that the fake modem's `Transmit` return value may be ignored by a core that plans with `PlanBurst`; the `transmit` output is the observable. Knock-on outside the corpus: plan 5.1's `PlanBurst` row (duration, block count, bytes per block) gains preamble, block and flush durations, which the M0LTE.Ms110d modem port must expose additively (phase 2 owner to confirm the real modem can give a fixed per-block duration for every waveform and interleaver it supports).
2. **corpus-format.md section 6, `csma.mode`.** Add `ed3` as a third value, per the K-0107 coordinator ruling (ed3-compatible: jitter, one common LBT value equal to `lbt_wait_dcd_ms`, LBT rule 1 off).
3. **corpus-format.md section 4.6.** Register the timer names of section 3.2 (including `csma.vdcd_end`, section 4.3) and the `link_state` names of section 2.2 as their PRs land (no format change; listed so the registry grows in the order of section 9). Also state there the zero-duration rule of section 3.3 as a consequence of `ms` >= 1, so a C++ author reads it in the format and not only here.
4. **prng.md section 6.** Fill the TODO for `csma.jitter` with K-0080 and K-0084 in P3-2.
5. **New settings** of section 7.2, each in the PR that first needs it, as the format already requires.

No change is proposed to the input or output kinds. The ten inputs and eight outputs are sufficient for phases 3 to 6; phase 8 adds the station hook pair (section 10 item 13).

---

## 12. The host: driving the core in real time (P6-3a)

Added by P6-3a. The core of sections 1 to 8 owns no clock, thread, socket or modem. The host (plan D3, `M0LTE.S5066.Host`) is the code that gives it those, for one node, without changing a single trace rule: a real node fed the inputs of a T2 trace in real time behaves as the trace says. The host is modem-agnostic; the M0LTE.Ms110d adapter (12.6) is one implementation of its modem port.

### 12.1 Shape

`NodeHost` holds one `Node`, one modem (12.5) and one SIS server (12.7). Every call on the `Node` happens on one serialising loop: an async loop task that takes one input at a time and runs it to completion, so the core never needs a lock. Other threads (socket readers, the audio thread, the modem's transmit completion) never touch the core; they post events into the loop's queues and return.

```
 socket readers --SisConnect/SisBytes/SisDisconnect--+
 modem threads  --BurstStarted/BytesReceived/...-----+--> queues --> loop --> Node.Handle --> outputs
 timer table    --TimerExpired (at the deadline)-----+                                          |
       ^                                                                                         |
       +------ TimerStart / TimerCancel <------------------------------------------------------- +
               Transmit, TerminateReceive, SetMaxInputBlocks --> modem
               SisBytesOut, SisClose --> SIS server;  LinkState --> host event (status, logs)
```

### 12.2 Clock

- Host time is a monotonic millisecond count (a `TimeProvider` timestamp, never the wall clock, which can step). It is the core's `T` directly: host time 0 is core time 0.
- At start the host reads UTC once, floors it to a whole second and writes that second into `time.epoch_year`, `time.epoch_day` and `time.epoch_second` of the configuration it gives the core (section 3.1), whatever its file says. Host time 0 is that whole second, so the first input is at the sub-second offset, never negative. TTDs on the wire are then real UTC (A-0162, A-0163) with no other conversion in the core.
- Events are stamped with host time when the loop receives them, never with a time the event claims for itself (a modem's sample index, for example). M0LTE.Ms110d's design reaches the same rule from the other side: every timestamp handed to the stack is on the host clock (its docs/design-5066-support.md 3.1). A stamp is never earlier than the `T` of the input the loop handled last, so `T` never decreases (corpus-format.md 4.4).

### 12.3 Timers

The host keeps the harness's timer table: name to (deadline, start sequence), filled from `TimerStart` (deadline = the input's `T` plus `ms`; a restart replaces the entry and takes a new sequence number) and emptied by `TimerCancel` and by expiry. A due timer is handed to the core as `TimerExpired` with `T` equal to its deadline, even when the loop reaches it late, because the core checks the deadline (section 3.3). The loop sleeps until the earlier of the next deadline and the next posted event. The timer table is the only timer: no per-timer OS timer is created, so a restart or cancel costs nothing and cannot race.

### 12.4 Same-millisecond ordering

The loop always hands the core the pending input with the smallest key (T, class, sequence), where the classes are those of corpus-format.md 4.5: 0 configuration, 1 timer expiries (sequence = the order of their last `TimerStart`), 2 modem events, 3 SIS events (sequence = arrival order within each). A due timer competes with posted events through the same key, and a timer that an input starts with a deadline already passed (a loop running late) is picked up before any later event. So, whenever events arrive in the same millisecond as each other or as a deadline, the order is the one a trace must list, and before an input at `T` every timer due at or before `T` has expired, as harness-contract.md 2 checks. A configuration change made at run time goes in as class 0 with the whole merged record (section 2.1).

### 12.5 The modem port

The core's `IModemPort` has one pure call, `PlanBurst`. The host's modem port extends it with what a real modem does, all in plan 5.1's terms:

```csharp
public interface IHostModem : IModemPort
{
    void Start(IModemEvents events);                     // events may be raised from any thread
    void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver il, int k, bool eom);   // returns at once
    void TerminateReceive();                             // D.5.4.6 d; not used in phases 3 to 6
    void SetMaxInputBlocks(int n);                       // D.5.4.6 c; not used in phases 3 to 6
}
public interface IModemEvents
{
    void BurstStarted();
    void BytesReceived(ReadOnlyMemory<byte> bytes);      // one decoded block
    void BurstEnded(BurstEndReason reason, int wn, Interleaver il, int k, int? snrTenthDb, int blocks);
    void TransmitDone();
    void ChannelBusy(bool busy);                         // the optional external busy input (plan 7.3, P5-4)
}
```

Rules every modem port follows:

1. `PlanBurst` is pure and never early. Each of its durations is rounded up to a whole millisecond (preamble, block and flush separately), so every EOT computed from it (section 5.6) is at or after the true end (C-0891).
2. `BurstStarted` is raised once per burst the modem has locked to, not for a candidate it may still reject; it is the core's DCD rising. Then `BytesReceived` once per decoded block, in order, then exactly one `BurstEnded`. A burst that never locked raises nothing.
3. Half duplex: while it transmits, the modem raises no receive events.
4. `Transmit` starts one burst and returns. `TransmitDone` is raised once per `Transmit`, when the last sample has left the output device (with its known output latency). The core never asks for a second burst before the first is done (section 5.7).
5. Bytes are bytes: the bit order on the air is the modem port's business (C-0150).
6. `ChannelBusy` is the external busy input. A modem may raise it from a detector of its own; a host may wire another source to the same event. Nothing else is DCD.

The modem events go into class 2 in the order the modem raised them.

### 12.6 The M0LTE.Ms110d adapter

`Ms110dHostModem` consumes the published M0LTE.Ms110d package (0.6.0) and an audio channel (9600 Hz mono floats in and out; the host resamples if its device runs at another rate). Choices, all ours unless a row is cited:

- Bytes go to the modulator LSB first, and decoded blocks are packed back the same way (C-0150).
- Waveform numbers map one to one (plan 5.3); interleavers by name, ultra short, short, medium and long. Very short and very long are never sent (plan 5.3); asking for them is a configuration error. K is the `k` of the `Transmit` (7 throughout, plan 5.3). EOM as the `Transmit` says; the 110D EOT marker is always appended.
- `PlanBurst` is `Ms110dBurstPlan.Plan` converted per rule 1: preamble from the preamble symbols, block from the symbols of one interleaver block, flush from the rest of the audio (the leading mini-probe, the EOT marker and the pulse-shaping tail), each rounded up to a ms at 9600 samples/s. With the tail in the flush, `DurationMs` is the length of the modulated audio rounded up by under 3 ms, so `TransmitDone` lands at the planned end. The modem counts the 32-bit EOM inside the blocks (the core always asks for EOM, section 5.6), so a burst whose bytes fill its last block exactly gets one more block; `DurationMs` and the EOTs stay right because the planner stamps from `PlanBurst(totalBytes)` (section 5.6). M0LTE.Ms110d 0.6.0 implements 3 kHz waveforms 0 to 8 and 13; asking for another is a configuration error.
- `BurstStarted` is the demodulator's `BurstStarted` event (WID accepted). `BytesReceived` is each `BlockDecoded`, delivered at once; the bits after the EOM in the last block reach the core too and are ignored by its sync search (C-0973). `BurstEnded` maps `Eom` and `MaxInputDataBlocks` to eom, `Terminated` to terminated, and `SignalLost`, `SignalAbsent` and `NewPreamble` to signal_lost; its waveform, interleaver and K come from the lock and the SNR is the burst SNR in tenths of a dB, rounded.
- A burst that never locked raises nothing (the demodulator may report a completed burst with no lock). A `Transmit` while a burst is locked ends it at once as signal_lost with the blocks so far, so the core still sees exactly one `BurstEnded` (rule 2); the demodulator's own later events for that burst are dropped.
- `Transmit` gates receive and ends any locked burst on the loop, then hands the modulation to a worker thread and returns; the worker plays the audio when it is ready. So a long burst never holds up the loop (issue #80), the core stays single-threaded, and `TransmitDone` still comes through class 2 when the last sample is out (12.4). A configuration error (a waveform or interleaver the adapter refuses) still throws on the loop; if the modulator itself fails, the adapter logs one line and raises `TransmitDone` so the core is not left waiting.
- Receive is gated while transmitting and the demodulator is reset after each transmission, as pdn-soundmodem does. The reset happens on the audio thread, before the next received chunk, because the demodulator is only ever called from there.
- The adapter raises no `ChannelBusy`.

### 12.7 The SIS server

- Listens on a configured endpoint, by default 127.0.0.1 port 5066 (S-0007, S-0008, S-0009; localhost by default, plan 10.1).
- Accepts every connection, never refusing one for count: the 16-client limit is the SAP table's, enforced at bind (S-0010, S-0015). Connection numbers start at 1 and are never reused.
- Posts `SisConnect`, then `SisBytes` with whatever chunks the socket gives (the core's decoder is chunking-independent), then `SisDisconnect` when the socket ends for any reason, including a close the core asked for (the core ignores a disconnect for a connection it has closed).
- Writes each `SisBytesOut` through a per-connection queue drained by its own writer, so a slow client never blocks the loop or another client.
- A client that does not read (S-0026, interp S-0029): if the oldest unwritten output to a connection has waited 30 s, or more than 1 MiB is waiting to be written to it, the host closes the connection, which the core sees as a disconnect, so the client is unbound and its SAP freed (S-0016). Both bounds are fixed in code (no setting; plan 3.2 principle 5). 30 s is three times the 10 s keep-alive answer bound (S-0041), long enough for a busy host and short enough that a dead reader does not hold a SAP for long. 1 MiB is sixteen of the largest frames the 16-bit size field allows; a client that reads never comes near it, and one that floods requests and never reads the answers is closed long before 30 s (without it, such a client made the node hold 260 MB, issue #80).
- If accepting a connection fails for any reason but shutdown (for example too many open files), the server logs one line, waits 1 s and goes on accepting.
- At shutdown the core unbinds and closes every bound client (interp A-0059); the host then closes the connections that never bound, after flushing what is queued, so shutdown is not held up by an idle client.
- `SisClose` from the core: the host flushes what is queued for that connection, within the same 30 s bound, then closes it.

### 12.8 Loopback for tests

Two in-process media let several hosts talk in one process, both test aids in the host library so phase 6's T4 and `s5066-test` can use them too:

- `LoopbackMedium`, byte level: each `Transmit` reaches every other modem on the medium as one burst of blocks, timed in real time by a plan from the node's `modem.*` fixture (the T2 arithmetic, corpus-format.md 5): carrier detect after the preamble, a block at the end of each block time, the end of the burst after the flush, and `TransmitDone` to the sender then. A modem transmitting or already receiving does not hear a burst; a test can drop bursts per receiver. With small fixture numbers an ARQ exchange takes well under a second.
- `AudioMedium`, audio: every 20 ms each station hears the sum of what the others play plus a little Gaussian noise, so two hosts run over the real M0LTE.Ms110d adapter. A confirmed ARQ U_PDU at WN 6 short takes about 10 s.

The audio port is `IAudioChannel`: received audio is a continuous real-time stream of chunks, silence included, and a burst is played whole with a callback once its last sample is out. Besides the in-process medium, `UdpAudioChannel` carries the same stream as 20 ms datagrams of 32-bit floats, so two `s5066d` processes, or a host and an audio bridge, can be joined without a sound card. A sound card and PTT come from either pdn-soundmodem's s5066 section (P6-3b) or s5066d's own radio medium (12.11).

### 12.9 Console host

`s5066d` loads one JSON file (`HostConfig`): the corpus configuration keys of corpus-format.md section 6, merged over the corpus `defaults.json` built into the host, with `modem.*` ignored by a real modem and `time.*` overwritten (12.2), plus a `host` object:

| Key | Meaning |
|---|---|
| `host.sis_listen` | SIS endpoint, `address:port`; default `127.0.0.1:5066` (S-0007 to S-0009) |
| `host.modem.kind` | `ms110d` |
| `host.modem.audio_listen`, `host.modem.audio_peer` | UDP audio endpoints (12.8) |
| `host.modem.radio` | a sound card and CM108 PTT instead of UDP audio (12.11); never with the two UDP keys |

The core reads some settings only when an exchange needs them (7.1), and an exception from the core stops the host, so `HostConfig` and `NodeHost` refuse at load a configuration without `csma.mode`, `cas.max_links`, `cas.link_request_retries` or `cas.break_retries`, or without `csma.node_slot_position` in slotted mode: the standard gives none of them a value and none can be derived (interp B-0454, issue #83). A slot position outside 1 to `csma.num_cont_slots` is refused too, since the core checks that range only at the first slotted contention. `NodeHost` also refuses a `drc.default_speed` its modem cannot plan; `UpdateConfig` applies the same checks. Each refusal is one line naming the key. A `csma.cont_slot_width_ms` no longer than the modem's preamble at that speed gets a one-line warning in the host log, when the host is made and on `UpdateConfig`, not a refusal (`NodeHost.ConfigWarnings`, K-0048, issue #86): with the modem alone the preamble is the least the channel-sensing delay can be, so such slots collide, but an external busy input (K-0006, which pdn-soundmodem wires up, as does s5066d with `busy_detect`, 12.11) is ORed into DCD and can sense a burst sooner, and the host cannot know its delay. When the file has no `prng_seed`, the host draws one from the operating system, so two nodes started from copies of one file do not share CSMA jitter. It has no amateur radio or AX.25 anything; the node address comes from `local_address`. The file may carry `//` comments.

- `src/s5066d/example.json` is a starting point. It leaves CSMA at the standard's defaults (16 contention slots of 3 s, 30 s listen-before-talk after DCD, 3 s after VDCD; [Ed4 K Table 7, p.K-12]), which suit a real HF net but make every exchange slow: one confirmed message between two nodes took 36 to 87 s in the P6-3a review.
- `src/s5066d/bench-node1.json` and `bench-node2.json` are a pair for two nodes on one machine over UDP audio, with shorter CSMA values (4 slots of 1 s, 2 s after DCD, 0.5 s after VDCD) and the timers derived from them (an answer wait of 12 s). A slot must outlast the time the far modem takes to declare carrier, about 0.74 s over UDP audio (the 0.72 s preamble plus audio chunking); the first version had 200 ms slots, so every contention between the two nodes was a collision and long transfers stalled (issue #86). They give none of the margin the long DCD wait exists for [Ed4 K.4, p.K-12] and are not for use on air.
- `s5066d --trace <config>` logs one line per D_PDU sent and received (type, addresses, sequence numbers, window edges), timer started, fired and cancelled, MAC state and inter-sublayer primitive, and each burst's planned and actual end, plus audio running behind real time. Each line starts with host time in ms and, when the loop handled an input late, `+` and how late. The core's part is `Node.Trace`, a diagnostic sink that changes no output (issue #86).

### 12.10 The client library and the chat sample

`M0LTE.S5066.Client` (`SisClient`) is the application side of Annex S over TCP. It reads its connection all the time and queues what arrives without bound, so a slow application never stops it reading (S-0024, S-0025); it answers every keep-alive at once and sends unprompted ones at most every 120 s (S-0041, S-0043, interp S-0044, S-0045); it holds U_PDUs back while flow is off and treats flow as on after each bind (S-0020, S-0021, interp S-0027); and it refuses a U_PDU over the bind's MTU, since splitting is the client's job (A-0003). A send that asks for confirmation completes on the matching S_UNIDATA_REQUEST_CONFIRM or S_UNIDATA_REQUEST_REJECTED: the oldest waiting send to the same SAP and address whose U_PDU starts with the copy returned (the copy may be the first bytes only, S-0127, S-0129), so answers may come in any order. It also parses the node information message (docs/management-messages.md). An exception thrown by an application's event handler is caught and passed to its `HandlerFailed` event, so it never stops the client reading (issue #80). On S_UNBIND_INDICATION the `Unbound` handlers run before the sends waiting for confirmation fail.

`samples/HfChat` (`hfchat`) is HF Operator Chat (Annex O, matrix `O.tsv`) on the client: SAP 5, priority 12, ASCII lines ending CR LF within the MTU, ARQ with node confirmation to one node and non-ARQ to a group or the broadcast address, with source, time and confirmation shown and a short history. Its NOT STATED choices are interp O-0024 to O-0026.

### 12.11 s5066d on a radio

Added 2026-11-08 by the amendment to plan D3: a standalone `s5066d` on each machine drives a CM108 USB audio interface and its radio itself, with no pdn-soundmodem daemon, configuration page or KISS server. pdn-soundmodem's s5066 section (its PR #784) stays as the second way to run a node; the two share the host library and differ only in what sits under the modem port.

**Where the code lives.** `M0LTE.S5066.Radio`, a small project that only `s5066d` references, so `M0LTE.S5066.Host` (a published package) has no sound card code and no new dependency. It references the published `pdn-soundmodem` package on nuget.org (0.100.0), the same package pdn-mailcast uses, for three things: ALSA capture and playback (`AlsaAudioInput`, `AlsaAudioOutput`, over `libasound.so.2` from the operating system), CM108 HID GPIO PTT (`Cm108Ptt`, writing the hidraw output report) and the energy busy detector (`EnergyBusyDetector`); and, through it, `M0LTE.Dsp` for the resamplers. Nothing amateur or AX.25 is used, although the package carries such code. That package is AGPL-3.0-or-later AND GPL-3.0-or-later AND GPL-3.0-only, so `s5066d` as built is distributed under the AGPL combined with those GPL-3.0 terms (the licences allow it, AGPL section 13), and the radio project is not packed or offered under the commercial licence. `HostConfig` passes `host.modem.radio` through untouched as `HostModemSettings.Radio`; `RadioSettings` reads it.

**The audio path** (`RadioAudioChannel`, an `IAudioChannel`) ports the logic of pdn-soundmodem's `S5066Station.ChannelAudio`:

- Both devices open at `sample_rate` (48 kHz by default, any multiple of 9600 from 19200). Received audio is read in 20 ms pieces and decimated by the whole factor to the modem's 9600 Hz; the 110D waveform sits at the same audio frequencies either side, so this is a resampler and nothing else.
- A burst is upsampled in `Play`, on the modem's worker thread, before the radio is keyed, with a fresh upsampler each time. Then a transmit thread raises `Keyed(true)`, keys PTT, writes `txdelay_ms` of silence, the burst and `txtail_ms` of silence in one write, drains the device, unkeys, raises `Keyed(false)` and only then reports the burst played, between two receive callbacks. So `TransmitDone` is the moment the tail has left the device, as rule 4 of 12.5 asks.
- TXDELAY does not disturb the EOTs: each is stamped relative to its own D_PDU's place in the burst (5.6), so a burst that starts later ends correspondingly later. It does lengthen the channel-sensing delay (K-0048): with the modem alone, the far node can sense us only `txdelay_ms` plus the preamble after our key. `RadioMedium.Warnings` gives a one-line warning when `busy_detect` is off and `csma.cont_slot_width_ms` is longer than the preamble but not longer than that sum (the host already warns for the preamble alone).
- A PTT that fails to key: one log line, nothing played, the burst still reported played so the core is not left waiting. A playback or capture device that fails: one log line, the radio unkeyed, and `s5066d` exits with status 1, so a service manager can restart it. On SIGTERM, SIGINT, SIGHUP or SIGQUIT `s5066d` stops in order, cutting short a burst in progress within 100 ms, and unkeys, since a CM108 holds its GPIO where it was last set.
- The radio is never left keyed by anything the process can see. The keyup is unkeyed on every way out, whatever is thrown, and an exception on the capture or transmit thread stops the channel (and `s5066d`, status 1) rather than the process. A transmit guard, a timer set at each key, unkeys the radio and stops the channel if the keyup is still keyed 3 s after its own length, which catches a playback device that hangs; the transmit thread cannot key again after it. A keyup whose burst is longer than `RadioAudioChannel.MaxBurstMs` (the core's 127.5 s cap on a transmission plus 2.5 s, above the longest burst the modem can plan, which a test checks) is never keyed. A stop unkeys first, before it waits for or closes anything, and `s5066d` unkeys again on an unhandled exception and at process exit. What it cannot cover is a SIGKILL, a power cut or a hung kernel: there the CM108 keeps its pin, so on air also set the radio's own transmit time-out timer, if it has one. The next start unkeys at once, as the PTT is opened.

**The busy input** (`RadioBusy`, optional, `busy_detect`, off by default as plan A7 says). The received 9600 Hz audio is band-passed to 180 to 3420 Hz, as pdn-soundmodem's own MS110D modem does for its detector, and fed to `EnergyBusyDetector`, which compares 40 ms block power with a slowly adapting noise floor (busy at 6 dB over it, with hysteresis and a hold). It suits an additive path such as SSB. As pdn-soundmodem's s5066 busy input does, our own transmission is left out and so are the 250 ms after each unkey while the receiver recovers; neither is fed to the detector, so neither moves its noise floor. The answer is raised as `IModemEvents.ChannelBusy` on each change, never as DCD. `RadioHostModem` is the wrapper that raises it; it implements the host's new `IHostModemWrapper`, through which `NodeHost` still finds the `Ms110dHostModem` inside to give it the trace sink.

**Logging.** At open, a sound card that would not take the buffer asked for says so in one line. A burst during which the playback device underran (a gap on air, the sign of a Pi too busy to keep its 120 ms buffer full) and each capture overrun (received audio lost) are one line each. One line per burst: `radio: tx wn6 short 25 bytes` once the burst has gone out, and `radio: rx wn6 short 512 bytes, SNR 12.3 dB` (with `, cut short` when it did not end at its EOM) when a received burst ends. The optional `rx_record` writes every received sample, at 9600 Hz, to a 16-bit WAV file whose header is kept current about once a second, for checking levels and passband on the bench.

**Settings** (`host.modem.radio`; each refusal is one line naming the key, as for the node keys):

| Key | Meaning | Default |
|---|---|---|
| `capture_device` | ALSA capture device, for example `plughw:CARD=Device,DEV=0` | required |
| `playback_device` | ALSA playback device | required |
| `sample_rate` | rate both devices open at, a multiple of 9600, 19200 to 192000 | 48000 |
| `ptt_device` | the CM108's hidraw node, for example `/dev/hidraw0` | required |
| `ptt_gpio` | the CM108 GPIO pin wired to PTT, 1 to 8 | 3 |
| `txdelay_ms` | silence after the key, before the burst, 0 to 2000 | 250 |
| `txtail_ms` | silence after the burst, before the unkey, 0 to 1000 | 20 |
| `tx_level` | the modulator's amplitude, above 0 and at most 1 | 0.5 |
| `busy_detect` | energy on the channel feeds the external busy input | false |
| `rx_record` | WAV file for all received audio | none |

`src/s5066d/radio-ft450.json` and `radio-ft818.json` are a pair for the two-radio bench (an FT-450 and an FT-818, one Raspberry Pi each): the CSMA and timer values of `bench-node1.json`, `busy_detect` on (the 1 s slots rely on it), a 300 ms TXDELAY, a low `tx_level` for the FT-450's 60 mVp-p data input, and placeholders for the device names. `docs/s5066d-radio.md` is the user's guide.
