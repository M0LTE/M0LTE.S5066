# STANAG 5066 over MIL-STD-188-110D Appendix D

Architecture, design and implementation plan. Version 1.2, 2026-11-07. Decisions D1 to D8 settled (section 13); phase 0 complete.

History:
- Draft 2 corrected every citation after an independent check (`notes/plan-review.md`).
- Draft 3 made the plan general-purpose: nothing amateur-specific is built into the node.
- Version 1.0 adds a dedicated 5066 test client as the test bed (section 11.6).
- Version 1.1 records Tom's decisions and the project organisation (sections 13 and 15).
- Version 1.2 (end of phase 0) applies the corrections collected in issue #9 and points at the matrix as the authority on interpretations.

## Summary

We have a working MIL-STD-188-110D Appendix D modem (M0LTE.Ms110d in C#, plus a C++ port). The data link that the military pairs with it is STANAG 5066. 110D itself says that "when an ARQ protocol is used it shall be in accordance with STANAG 5066" (110D main body 4.4, p.15). STANAG 5066 provides reliable delivery (selective ARQ), best-effort broadcast, automatic data rate selection, channel access on a shared frequency, and a standard application interface (the SIS, on TCP port 5066).

This plan builds a STANAG 5066 Edition 4 node over our modem, as close to the standard as the published text allows. It is a general-purpose implementation, not an amateur radio one. Application developers get the native 5066 SIS interface and nothing else: no KISS, ARDOP or VARA idioms. Nodes are identified by ordinary 5066 node addresses, set in configuration. Anything deployment-specific, such as mapping names or callsigns to addresses, regulatory identification, or bridges to other systems, is an external concern. It is handled by a separate SIS client above the node, or by an optional hook in the station layer below it.

Three things are ours to design because the standards leave them open:

1. The data rate choice algorithm.
2. The modem control interface.
3. CSMA tuning for real channels.

The work is in ten phases. The C# implementation is built test-first against a language-neutral test corpus, so a C++ port can later prove itself against the same files.

Tom's decisions are recorded in section 13, and the project organisation in section 15.

## Contents

1. Sources and how this plan cites them
2. What the standards say we are building
3. Architecture
4. Profile and compliance scope
5. Modem integration
6. Data rate selection
7. Channel access on a shared frequency
8. Addressing
9. Optional station-layer hooks
10. The application interface, and how users and developers use it
11. Test design, including the C++ port
12. Implementation phases
13. Decisions needed from Tom
14. Risks and known gaps in the standards

---

## 1. Sources and how this plan cites them

| Short name | Document | Status |
|---|---|---|
| Ed4 A, B, C, D, F, J, K, N, O, S, U, V | AComP-5066 Edition A Version 1 (STANAG 5066 Edition 4) annexes, as hosted by Isode | Marked "NATO UNCLASSIFIED, Releasable to Interoperability Platform". Annex F is "DRAFT 1.3" and marked "Releasable to the Public". |
| Ed4 main | STANAG 5066 Edition 4 main body, draft v1.6 of 27 August 2021 | "Releasable to the Public, DRAFT FOR REVIEW" |
| Ed1 | STANAG 5066 version 1.2 Annexes A, B, C (public, n2ckh.com mirror) | Public. Used only where Ed4 refers back to Ed3 and Ed3 is unavailable. |
| 110D | MIL-STD-188-110D, 29 December 2017 | Public |
| JITC TP | DISA JITC "STANAG 5066 Test Procedures for HF Radio Data Communications", April 2004 | Public. Tests Edition 1 v1.2. |
| OFW611 | Ofcom Amateur Radio Wireless Telegraphy Licence Conditions Booklet, 21 February 2024 | Public |

Citations take the form [Ed4 C.6.5, p.C-71]. Page numbers are reliable. Some Ed4 section numbers were lost in text extraction and recovered from rendered pages.

The extraction notes these citations come from are in `notes/`: `sis-addressing.md`, `dts-drc.md`, `cas-mac.md` and `modem-and-tests.md`. Every fact in them carries a page citation, and anything the documents do not say is marked NOT STATED.

**Edition 3 is not available to us.** Isode states that Edition 3 was publicly distributable and later editions are not. Edition 4 is published as AComP-5066 Edition A, obtainable through a NATO representative (Isode white paper "STANAG 5066 Edition 5", p.2). We could not find a copy of Edition 3. Edition 4 itself describes everything needed to interoperate with Edition 3 peers at the level this plan targets. The few places where Ed4 refers out to Ed3 text are listed in section 14.

**Rule for this project:** we cite the Ed4 annexes by section and page, and paraphrase. We never copy their text, figures or tables into our repositories, and the PDFs themselves stay out of the repositories. See decision D1.

---

## 2. What the standards say we are building

### 2.1 The STANAG 5066 stack

STANAG 5066 is a stack of sublayers [Ed4 main 2.2, Figure 1, p.3]:

| Sublayer | Annex | Job | PDU |
|---|---|---|---|
| Subnetwork Interface Sublayer (SIS) | A (service), S (access protocol) | The application interface. Up to 16 clients, one per SAP ID 0-15. Unicast and multicast message delivery (U_PDUs), ARQ or non-ARQ, priority, time to live, delivery confirmation. | S_PDU |
| Routing Sublayer | R (optional) | Relay across partially connected networks | R_PDU |
| Channel Access Sublayer (CAS) | B | Makes and breaks "physical links" between pairs of nodes (CAS-1 linking), negotiates Edition 4 features | C_PDU |
| Data Transfer Sublayer (DTS) | C | Segmentation into D_PDUs, selective ARQ, non-ARQ with repeats, EOT timing, data rate selection | D_PDU |
| Media Access Control (MAC) | J, K (CSMA), L (token ring) | Who transmits when on a shared channel | M_PDU |
| Modem interface | D (synchronous serial), or another mechanism such as 110D Appendix A over TCP [Ed4 main 3.1, p.4]. Annex T is a TRANSEC sublayer that replaces Annex D [Ed4 main 3.3, p.6]. | Bytes to and from the waveform | |

### 2.2 How the pieces fit our modem

- **Auto-baud is the key enabler.** The 110D Appendix D preamble carries the waveform number, interleaver and constraint length, so the receiver sets itself up from each burst [110D D.4, p.141; D.5.2.1.3.2, p.167]. Edition 4 data rate selection requires an auto-baud waveform and names STANAG 4539 and STANAG 5069 [Ed4 C.7.6.2, p.C-119].
- **The sender decides the rate.** In Edition 4 there is no rate change handshake. For each transmission the sender chooses speed, interleaver and segment size. Receivers send unacknowledged recommendations in the header "EOW" field (MAX-SPEED, LOW-SPEED, MAX-SEGMENT) [Ed4 C.7.6.2, C.7.6.3, pp.C-119 to C-120]. This fits an auto-baud modem exactly, because no state change has to be coordinated.
- **Edition 4 is the right edition for this waveform.** Edition 3 can only express narrowband rates in a 4-bit field. Edition 4 adds a speed byte whose values 1-13 are "STANAG 5069 waveform numbers" [Ed4 C.6.5.1, Figure C-50, p.C-72], and a 16-bit frame sequence number for larger ARQ windows [Ed4 C.7.2 (g), p.C-99].
- **STANAG 5069 and 110D Appendix D.** Secondary sources describe STANAG 5069 as the NATO equivalent of the 110D Appendix D wideband waveforms (sigidwiki "STANAG 5069"; Isode press material). We have no primary text confirming that 5069 waveform numbers equal 110D Appendix D WNs. This plan assumes they are equal (assumption A1, section 14).

---

## 3. Architecture

### 3.1 Overview

```
 +-----------------------------------------------------------+
 |  Applications (SIS clients, any language, TCP port 5066)  |
 |  HF chat (Annex O), CFTP (Annex V), IP client (Annex U),  |
 |  external adapters (name mapping, BBS bridge, ...)        |
 +---------------------------+-------------------------------+
                             | SIS Access Protocol (Annex S)
 +---------------------------v-------------------------------+
 |  s5066 node (library + host process)                      |
 |   SIS (A) -> CAS (B) -> DTS (C) -> MAC/CSMA (J, K)        |
 |   Rate selection policy (ours, inside DTS rules)          |
 +---------------------------+-------------------------------+
                             | Modem port (our interface)
 +---------------------------v-------------------------------+
 |  Station layer (outside the 5066 interface)               |
 |   M0LTE.Ms110d modulator/demodulator                      |
 |   Audio in/out, PTT/CAT, optional ID hooks               |
 |   Optional external busy input                            |
 +-----------------------------------------------------------+
```

### 3.2 Design principles

1. **Sans-IO protocol core.** Every sublayer is a deterministic state machine. Inputs are received bytes, modem events, SIS requests and timer expiries. Outputs are actions such as "send these bytes", "deliver this U_PDU" and "start timer T for N ms". Time comes from an injected clock, and randomness (CSMA jitter) from an injected, specified pseudo-random generator. There are no threads, sockets or wall-clock reads inside the core. This is what makes byte-exact cross-language testing possible (section 11).
2. **The standard's layering is the code's layering.** The SIS, CAS, DTS and MAC are separate modules with the service boundaries the annexes describe, including the D_ and C_ primitives as internal interfaces. The annexes mark those primitive sets as information only [Ed4 Table B-1, p.B-4]. We adopt them anyway, because they make the code traceable to the text.
3. **One requirement, one place.** Each "shall" we implement gets an ID in a conformance matrix, which links to the code that implements it and the tests that prove it (section 11.2).
4. **Nothing deployment-specific in the protocol core.** Addresses are plain 5066 addresses from configuration. Name or callsign mapping, regulatory identification and bridges are external, either SIS clients or optional station-layer hooks. None of them changes a 5066 PDU.
5. **No unrequested settings.** Where the standard says a value is "configurable", the code accepts it in configuration, but we ship one documented value. We do not invent extra knobs.

### 3.3 Components and repositories

| Component | Contents | Language |
|---|---|---|
| `M0LTE.S5066.Core` | Codecs (D_PDU, C_PDU, S_PDU, S_primitive framing, EOW, addresses, CRCs) and the sans-IO state machines (SIS, CAS, DTS, MAC) | C#, later C++ |
| `M0LTE.S5066.Client` | SIS client library for application developers. Annex S framing and every client obligation (section 10.2), plus an optional confirmation-matching helper | C#; other languages later |
| `M0LTE.S5066.Station` | Modem port over M0LTE.Ms110d, PTT/CAT and audio glue, and the optional hooks of section 9 | C# |
| Host (D3) | A 5066 host server inside pdn-soundmodem, beside the KISS server, as the ARDOP work did with M0LTE.Ardop. It wires the core to sockets, timers and the station, and serves the SIS on TCP 5066. No AX.25 in its path. Amended 2026-11-08 (section 13): the standalone `s5066d` also drives a sound card and PTT itself. | C# |
| `s5066-test` | The dedicated test client: SIS test driver (chat, file, perf, scripted scenarios) and a receive-only 5066 monitor (section 11.6) | C# |
| `s5066-conformance` | Language-neutral test corpus (vectors, scenario traces), the conformance matrix and the harness specification | JSON and plain text |

Repos (D2): GitHub M0LTE/M0LTE.S5066 (libraries, plan, status, tracking issues) and M0LTE/s5066-conformance (corpus, matrix). Licence AGPL-3.0-or-later plus commercial, like M0LTE.Ms110d.

---

## 4. Profile and compliance scope

### 4.1 Layer Services Profile

We implement the Edition 4 profile **"CSMA Fixed Frequency"** [Ed4 main 7.2, Table 2, p.13]. Its description is "Multi-node using Annex K", "Useful for Surface Wave with light load or Skywave deployment without ALE". It makes Annexes A, B, C, D and S mandatory, plus the MULTIPLE ACCESS option, Annex J and Annex K [Ed4 main 7.2, pp.12 to 13].

Why this profile:

- Only two profiles need neither ALE nor duplex: "CSMA Fixed Frequency" and "WTRP Fixed Frequency" (MULTIPLE ACCESS, Annex J, Annex L) [Ed4 main Table 2, p.13]. WTRP is aimed at "Surface Wave with high load", and every node must take part in the token ring. That suits a closed, coordinated network better than an open shared channel where stations come and go and other users share the frequency (our judgement). CSMA needs no extra protocol, and Annex K targets lightly loaded networks with two active nodes [Ed4 K.2.1, p.K-1].
- On a single shared frequency without ALE, Annex B leaves Multiple Simultaneous Peer Access as the only access model. That model needs a MAC [Ed4 B.4 to B.6, pp.B-6 to B-8].
- Annex J requires CSMA even between two nodes [Ed4 J.3.1, p.J-3]. Annex R says a MAC is not needed for two nodes [Ed4 R, p.R-4]. We follow Annex J.
- **Caution: CSMA is not free between two nodes.** Annex K's two-node rule sets only the listen-before-transmit wait to zero [Ed4 K.3.2.2, p.K-9]. As written, the random contention wait still follows [Ed4 K.3.2.3, p.K-10]. With the default 3 s slots and 16 slots, that averages 22.5 s per turn. Only the informative text says that for two nodes "slots are not needed at all" [Ed4 K, "Slotted Mechanism", p.K-6]. See section 7.2 and decision D7.
- Annex J is headed "(Informative)" but the profile makes it mandatory, so its "shall" statements are treated as binding [Ed4 J, p.J-1; main Table 2, p.13].

All nodes in a deployment must use the same profile [Ed4 main 7.2, p.12].

### 4.2 Application profile

The application profile must list at least one application [Ed4 main 7.3, p.14].

- **First application: HF Operator Chat** [Ed4 Annex O]. It is small and fully specified: SAP 5, priority 12, CR LF terminated IA5 text [Ed4 O.2, O.3, pp.O-1 to O-3]. That makes it the ideal first end-to-end proof.
- **Later applications:** CFTP for files and mail [Ed4 Annex V, SAP 12] and the IP client [Ed4 Annex U, SAP 9, IPv4 and IPv6].

### 4.3 What we implement and what we do not

| Item | Decision | Basis |
|---|---|---|
| SIS core primitives (bind, unbind, unidata, confirm, reject) | Implement | Ed4 A, S mandatory |
| SIS local primitives (flow on and off, keep-alive, subnet availability, management message) | Implement | Ed4 S.4 |
| Hard links (S_HARD_LINK_*, CAS LINK bit) | Not implemented. Reject the LINK bit with reason 5. | Deprecated profile option HARD LINKS [Ed4 main Table 1; B.7.1.2, p.B-16] |
| Expedited data user service (S_EXPEDITED_*) | Not implemented | Deprecated profile option EXPEDITED DATA [Ed4 main Table 1] |
| D_PDU type 8 (expedited non-ARQ) | Implement. CAS-1 C_PDUs types 1-5 travel in it. | Ed4 B.7.1.2 to B.7.1.6 |
| D_PDU types 4 and 5 (expedited ARQ) | Implement receive and acknowledge for robustness. No procedure in our profile sends on expedited ARQ: only the ALE C_PDUs 7 to 10 do. | Ed4 B.7.2.9, p.B-37; B.7.1.11, pp.B-24 to B-25 |
| D_PDU type 6 (MANAGEMENT) | Implement: stop-and-wait, one outstanding, acknowledged with a type 6 carrying the ACK flag, precedence over other D_PDUs | Ed4 C, MANAGEMENT D_PDU section, pp.C-43 to C-45 |
| Ed4 ARQ family (types 9-12), EXTENSION (13), PADDING (14) | Implement | Ed4 Table C-2, p.C-8 |
| Ed3 ARQ family (types 0-3) | Implement, used only towards peers found to be Edition 3, or if both peers negotiate Short | Ed4 C.4.3, p.C-22; B.7.2.2 |
| Ed4 rate EOWs (8 MAX-SPEED, 9 LOW-SPEED, 10 MAX-SEGMENT, 11 SENDER-APPROACH, 12 SPEED-USED, 15 CAPABILITY) | Implement | Ed4 C.6.5 to C.6.10 |
| Ed3 rate EOW (1 ED3-BASIC-RATE) as advisory to Ed3 peers | Implement (a "shall" towards Ed3 peers), with Other Parameters set to binary 10 (value 2, "DRC Advisory"). Needs our own table from 110D WN to the 4-bit Data Rate code (Table C-5) and the interleaver code (Table C-6: none, short, long). | Ed4 C.6.2, Tables C-5 to C-7, pp.C-68 to C-70; C.7.6.3.2, p.C-122 |
| EOW 0 EMPTY, EOW 3 UNRECOGNIZED-TYPE-ERROR | Implement (send type 3 on receipt of an unknown EOW, as the annex says we "should") | Ed4 C.6.1, p.C-68; C.6.3, p.C-70 |
| Full Ed3 stop/start DRC interworking | Not implemented. Answer an Ed3 DRC or Annex I request with a WARNING, as Ed4 requires when the optional interworking is not implemented. | Ed4 C.7.6.1.1, p.C-118 |
| High Data Rate Change Request PDU | Not implemented | Deprecated HDRCR [Ed4 main Table 1; C.9.1] |
| Non-ARQ with errors | Not implemented | Not part of the CSMA Fixed Frequency profile [Ed4 main Table 2, p.13] |
| ALE (all of B.3, B.6.2 to B.6.4, C_PDU types 7-10) | Not implemented | The ALE profile options (ALE 1:1, MULTICAST ALE, DUPLEX WITH ALE, MAC ALE) are not in our profile [Ed4 main Tables 1 and 2, pp.11, 13]. Without ALE, CAS-1 linking is used [Ed4 B.7, p.B-12]. |
| C_PDU type 6 PHYSICAL LINK BREAK REQUEST and the Clean Close procedure | Implement, towards Ed4 peers only. Type 6 travels on regular ARQ, not type 8. | Required by the non-ALE break procedure [Ed4 B.7.2.8 via B.7.2.4, pp.B-31, B-35]; B.7.1.7, pp.B-22 to B-23 |
| Annex L WTRP | Not implemented. A received WTRP D_PDU draws a WARNING, as Annex J requires. | Ed4 J.4.1, p.J-5 to J-8 |
| Annex R routing, Annex T TRANSEC | Not implemented. Annex T would also clash, for amateur deployments, with OFW611 clause 19(b), which forbids encryption "for the purpose of obscuring" meaning. | Optional [Ed4 main 7.2, p.12]; OFW611 clause 19(b), p.7 |
| Annex D synchronous serial interface | Not implemented. We use our own modem port (section 5). Annex D says the DTS-to-equipment interface "shall" be Annex D or Annex T [Ed4 D, p.D-1], but the main body allows "other communication mechanisms" [Ed4 main 3.1, p.4] and Annex C says STANAG 5066 "does not specify or require an interface here" [Ed4 C.7.7.3.1, p.C-124]. Recorded as an interpretation in 4.4. The Annex D rules that still make sense are kept as matrix rows: nothing between D_PDUs, EOT covers any post-fill, the receiver searches for the sync. | Ed4 D, p.D-1 |
| Deprecated SIS primitives received (hard link 6 to 13, expedited 24 to 27) | Treated as invalid primitives. Repeated ones lead to unbind reason 3, "Too many invalid primitives". | Ed4 A.2.2.5, p.A-7 (our reading) |

### 4.4 Ambiguities in Ed4 and how we resolve them

Each resolution goes in the conformance matrix as a documented interpretation. Phase 0 found many more than are listed here: the complete, authoritative set is every row with strength `interp` in M0LTE/s5066-conformance `matrix/*.tsv` (over 150 rows). This table keeps only the ones that shape the design.

| Issue | Our reading |
|---|---|
| S_DATA_FLOW_OFF prints as type 15, the same code as FLOW_ON [Ed4 S Fig S-12, p.S-14] | Use 16, as in Ed1 [Ed1 Fig A-17]. Annex S says the protocol is "unchanged from Edition 3" [Ed4 S.7, p.S-21]. |
| State tables and the FULL RESET text often name only the Ed3 types [Ed4 Tables C-9 to C-24; C.7.4, pp.C-108 to C-109] | Not a real ambiguity: Annex C says the DATA state logic is the same for each family, described with generic names, and the families are not mixed [Ed4 C.7.2, p.C-97]. Apply to the negotiated family. |
| RESET RX WIN CMND direction wording conflicts within C.4.7.3 [pp.C-38 to C-39] | Settle with a worked two-node scenario test before implementing. Record the reading. |
| RLE acknowledgement bit reference point [Ed4 C.4.5.5, p.C-32] | First bit = RX LWE + 1 + Offset x 256, by analogy with the type 1 bitmap rule [p.C-30] |
| Over-MTU U_PDU rejection without confirmation requested [Ed4 A.2.2.8, p.A-10 vs A.3.2.3, p.A-22] | Always reject with reason 4. A.2.2.8 is the more specific rule and costs the client nothing. |
| DATA DELIVERY FAIL byte 0 low nibble labelled "Priority" [Ed4 Fig A-8] | Follow the text: the second field is REASON [Ed4 A, p.A-18], as in Ed1 |
| DATA DELIVERY CONFIRM byte 0 low nibble: Ed4 figure and text say its fields equal those of the DATA S_PDU, so Priority [Ed4 A, p.A-17, Fig A-6]; the Ed1 figure says NOT USED | Send the DATA S_PDU priority. Ignore the nibble on receipt. |
| Type 4 BREAK reason labels 1, 2, 5 clipped in the published PDF [Ed4 B.7.1.5, p.B-20] | Use Ed1 labels for 1 and 2. Never send 5. |
| CAPABILITY EOW cited as C.6.4 in places | It is C.6.10, EOW type 15 [p.C-75] |
| CSMA two-node case: the informative text says two nodes need no contention slots [Ed4 K, p.K-6]; the normative transition still sets a contention wait [Ed4 K.3.2.3, p.K-10] | Decision D7 |
| TTL 0: "shall define an infinite TTL", but the same paragraph computes the deadline from the default maximum when TTL is 0 [Ed4 A.2.2.6, p.A-9]. The field maximum is 648000 (about 15 days) [Ed4 S Fig S-15, p.S-15]. | TTL 0 = the node's configured default maximum, which we set to the field maximum. In practice that is "forever" for HF traffic, and the queue stays bounded. |
| Address in S_primitives: right-aligned per S.5.1 [p.S-8], but Fig S-19 says only the first SIZE half-bytes are valid [p.S-19] | Send right-aligned with SIZE = 7 (full 28 bits) and the unused nibbles zero, so both readings agree. Decode right-aligned. |
| Ed4 EOWs (including CAPABILITY) "shall not be used" towards peers whose support is unknown [Ed4 C, Table C-4 notes, p.C-67], yet the main body uses the CAPABILITY EOW to discover Ed4 support [Ed4 main 6, p.10]. PADDING is Ed4-peer only too [p.C-53]. | Discover editions by CAS-1 negotiation only. Send CAPABILITY and PADDING only to peers known to be Ed4. Non-ARQ broadcasts to unknown receivers carry no Ed4 EOWs and no PADDING. |
| Extended D_PDU types given to WTRP: 1 to 4 [Ed4 C.8, p.C-124] versus 1 to 6 [Ed4 C.7.5.1, p.C-110] | We run no WTRP. Any EXTENSION D_PDU draws a WARNING with reason 4 [Ed4 Table C-3, p.C-55]. |
| Annex D "shall" versus the main body and Annex C allowing other modem interfaces (section 4.3) | Use our modem port. Keep the Annex D rules that apply in spirit as matrix rows. |
| Identification hook output (section 9.1) | Sent as its own transmission under Annex K, never inside a 5066 transmission, so no EOT interpretation is needed. Annex K's carrier-sense rules apply to it as they do to data [Ed4 K.1, p.K-1]. |

---

## 5. Modem integration

### 5.1 What the stack needs from the modem

STANAG 5066 does not specify modem control [Ed4 C.7.7, p.C-123; main 3.4, p.7]. The main body names three needs [Ed4 main 3.4, p.7]:

1. The sender sets speed and interleaver per transmission.
2. The receiver reports SNR and other receive characteristics.
3. The modem reports end of transmission when no EOT field is parsed.

110D Appendix A's TCP interface does not meet these needs. It carries data and reports the received rate, but it has no command to set the transmit waveform and no SNR [110D A.5.1.1.8, A.5.1.1.9, A.5.1.2.1, pp.49 to 52]. The 110D D.5.4.6 remote control list (bandwidth, waveform ID, interleaver, constraint length, EOM on or off, maximum input data blocks, terminate reception) has no defined wire format, and no SNR item either [110D D.5.4.6, pp.239 to 240; 5.3.1.5, p.18].

So we define our own **modem port**. It is an in-process interface, and it can also be carried over a socket later if a remote modem is ever wanted. Its contents:

| Operation or event | Purpose | Source of requirement |
|---|---|---|
| `Transmit(bytes, wn, interleaver, k, eom)` returning the planned duration | One 5066 transmission = one modem burst | 110D D.5.4.6 a, b; Ed4 main 3.4 item 1 |
| `PlanBurst(wn, interleaver, byteCount)` returning duration, preamble, per-block and flush durations, block count and bytes per block | EOT computation [Ed4 C.4.2.3, p.C-13] and modem block alignment [Ed4 C.7.5.3.1, pp.C-112 to C-113] | Ours |
| `TerminateReceive()` | D.5.4.6 d | 110D |
| `SetMaxInputBlocks(n)` at run time | D.5.4.6 c, for fixed-size ARQ bursts | 110D D.5.4.5.3, p.239 |
| `BurstStarted(time)` | Carrier detect for CSMA [Ed4 K.2.2.4, p.K-2] | Ed4 K |
| `BytesReceived(bytes, time)`, per decoded block | Feeds the D_PDU sync search | Ed4 C.7.7.3.2, p.C-124 |
| `BurstEnded(time, reason, wn, interleaver, k, snrDb, blocks)` | End of transmission and receive quality | Ed4 main 3.4 items 2, 3 |
| `TransmitDone(time)` | End of our own burst: the core needs it for EOT bookkeeping and the CSMA "last transmission by this node" rule [Ed4 K.3.2.3, p.K-10] | Ours (accepted in phase 0) |
| `ChannelBusy(bool)` from an optional external detector | Extension, section 7.3 | Ours |

### 5.2 Gaps in M0LTE.Ms110d to close first

From `notes/modem-and-tests.md` section D.5, checked against M0LTE.Ms110d v0.4.1. All of these must land in both C# and C++, so the port stays level:

1. **Receive SNR estimate per burst, and ideally per block.** It doesn't exist today. The mini-probes are known symbols [110D D.5.2.2, p.170], so an estimator from probe residuals after equalisation is the natural method. That method is our design, not the standard's. Validate it against the channel simulator at known SNRs.
2. **Burst start and end timestamps in C#.** The C++ port already reports a burst-started event and sample counts.
3. **Run-time maximum input data blocks.** It is currently fixed at construction.
4. **EOT detection** (optional in 110D [D.5.4.4, pp.236 to 237]). It allows faster turnaround. Lower priority.
5. **WN 9 to 12.** Needed for full WBHF Block 1 [110D D.4.1, p.141]. Not needed to start, because the rate ladder can cap at WN 8.

Already present and usable: autobaud lock info (WN, interleaver, K) per burst, the transmit duration estimate, block capacity in input bits, EOM framing and terminate.

### 5.3 Mapping 5066 speed codes to 110D Appendix D

| 5066 field [Ed4 C.6.5.1, Figures C-50, C-51] | Our mapping | Status |
|---|---|---|
| Speed 1-13 = "STANAG 5069 waveform number" | 110D Appendix D WN 1-13 at 3 kHz | Confirmed (A1): AComP-5069 Edition A Version 1 numbers its waveforms 0 to 13 with the same 3 kHz rates and modulations (Table B.3.1.2-1, p.B-5), and uses the same preamble WID and interleaver mapping (B.3.2.1.3.2) |
| Speed codes 17-30 (narrowband rates in bps) | Not used by us | We send WBHF codes only |
| WN 0 (75 bps Walsh) | Cannot be named in an EOW: 00000 is reserved. A sender may still choose WN 0, because a lower speed never exceeds a recommendation [Ed4 C.7.6.2 item 6, p.C-120]. | Our choice |
| Interleaver 001 Ultra Short, 011 Short, 100 Medium, 101 Long | 110D Appendix D Ultra Short, Short, Medium, Long [110D Table D-XVI, p.168] | Mapping by name; Annex C does not state it |
| Interleaver 010 Very Short, 110 Very Long | Never sent. On receipt, treat as the nearest Appendix D length. | Ours |
| Constraint length K | No field exists [Ed4 C.6.5.1]. Network-wide K = 7, which matches WBHF Block 1 [110D D.4.1, p.141]. | Ours |
| Bandwidth | Fixed at 3 kHz for the network. Ed4 says bandwidth is fixed for a sequence of transmissions [p.C-73], and Annex J says a fixed bandwidth "shall be the same for all nodes" [Ed4 J.5.1, p.J-8]. | Standard |

### 5.4 Transmission construction

A transmission is a sequence of D_PDUs sent as one modem burst. The rules we follow:

- Each D_PDU starts with the 0xEB90 Maury-Styles sync, sent LSB first (bytes `90 EB`) [Ed4 C.4.2, pp.C-11, C-19]. The receiver finds alignment by searching for it [Ed4 C.7.7.3.2, p.C-124].
- EOT is set in every D_PDU, counts half seconds remaining from the start of that D_PDU including sync, rounds up, and decreases monotonically. Maximum 127.5 s [Ed4 C.4.2.3, p.C-13; C.7.5.4.3, p.C-114]. We compute it from `PlanBurst`. The plan includes the preamble and modem flush.
- **Block alignment** (Ed4 peers only): D_PDUs should not straddle modem interleaver blocks [Ed4 C.7.5.3.1, pp.C-112 to C-113]. Block capacity is the 110D Table D-XXXVII "input bits" per block divided by 8 (for example WN 6 Short: 1536 bits = 192 bytes per block) [110D Table D-XXXVII, p.205]. Spare space is filled with ACK-ONLY or PADDING D_PDUs, or by choosing segment sizes [Ed4 C.7.5.4.2, p.C-114; C.5.4, p.C-65]. Towards Ed3 peers only the last segment may be short [Ed4 C.5.1 (e), p.C-58].
- The maximum segment size is at most 1023 bytes [Ed4 C.4.2.10, p.C-20] and must not vary within one transmission [Ed4 C.5.3, p.C-65]. Towards Ed4 peers, individual segments may be smaller than that maximum [Ed4 C.5.4, p.C-65].
- MTU 2048 for a general-purpose node [Ed4 A.2.2.3, p.A-6]. Annex C recommends an MTU of 2042 so that a C_PDU fills two 1023-byte segments [Ed4 C.5.1.1, p.C-61]. Annex A's "shall be 2048" wins. The client library documents the tip.
- **EOM:** kept on. The 110D receiver always scans for it [110D D.5.4.5.1, p.239], and with block alignment the last block is padded anyway. Revisit if measurements show that inhibiting EOM gains a block.

---

## 6. Data rate selection

### 6.1 What the standard fixes

- The sender chooses speed and interleaver per transmission. With several destinations, it must not exceed any destination's maximum recommended speed [Ed4 C.7.6.2, pp.C-119 to C-120; C.7.5.4.4, p.C-115].
- The receiver sends MAX-SPEED (best throughput, accepting a significant fraction of retransmissions), LOW-SPEED (low latency, very few errors) and MAX-SEGMENT (value x 4 bytes) as unacknowledged EOWs. It sends them only to Ed4 peers, and only in D_PDUs addressed to a unicast address [Ed4 C.6.5 to C.6.7, C.7.6.3, pp.C-71 to C-74, C-120].
- The sender may announce SENDER-APPROACH: fixed or variable, and a strategy of bulk, low latency, mixed or intermediate. Table C-25 says which recommendation the receiver should prioritise for each [Ed4 C.6.8, p.C-74; Table C-25, p.C-122].
- The sender may send SPEED-USED when the receiver cannot determine the received speed [Ed4 C.6.9, p.C-75]. Our modem always knows it, from the WID, so we do not ask for SPEED-USED.
- Guidance only: around 50% frame error rate maximises throughput [Ed4 C.5.1.1, p.C-60]. Segment size guidance runs from 100 bytes at 75 to 150 bps up to 1023 for wideband [pp.C-60 to C-61].
- No thresholds, hysteresis or algorithm are given: "The algorithms and choices made are left to the implementation" [Ed4 C.7.6.2, p.C-120].

### 6.2 Our algorithm (implementation choice, to be tuned by measurement)

Amended 2026-11-08: the phase 7 design in `docs/core-architecture.md` section 9 supersedes the receiver-side thresholds below. The modem's SNR estimate is post-equaliser and saturates on fading (M0LTE.Ms110d `docs/snr-calibration.md`), so thresholds are calibrated as block error rate against the estimate per waveform and interleaver, and moves up on fading need a clean run and a trial. The rest of this section stands.

**Receiver side**, per peer:

- Keep a smoothed SNR from `BurstEnded.snrDb` and a frame error rate from data CRC results.
- MAX-SPEED = the highest WN whose required SNR, plus a margin, is below the smoothed SNR.
- LOW-SPEED = the same with a larger margin.
- The interleaver recommendation comes from the measured fading rate. Start with "Long for bulk, Short for low latency".
- The starting SNR table is 110D Table D-LXIV. It gives the SNR for BER 1E-5 per WN on AWGN and "Poor" channels, measured with the Long interleaver [110D D.6.1, Table D-LXIV, pp.240 to 241]. For example WN 6 needs 9 dB AWGN and 14 dB Poor, and WN 1 needs -3 dB and 3 dB. Those figures are BER targets, not D_PDU error targets, so margins are calibrated in simulation (phase 7).
- The recommendation travels in the EOW of the D_PDUs we already send to that peer, and is repeated in PADDING when there is room [Ed4 C.4.14, pp.C-53 to C-54].

**Sender side:**

- Start each new link at a conservative default: WN 2, Long interleaver, until the first recommendation arrives. Ed4 lets the sender choose the initial speed [Isode Ed4 white paper, p.3; Ed4 C.7.6.2].
- Apply the minimum MAX-SPEED across all addressed peers. Step down by itself after a configured number of transmissions that go unacknowledged, because a receiver cannot report on bursts it never decoded.
- Choose transmission length by strategy. Long transmissions suit bulk, shorter ones suit latency [Ed4 C.7.5.4.1, p.C-113].

**Non-ARQ broadcast:** a fixed conservative speed per application, with repeats. Ed4 recommends more conservative rates for non-ARQ [Ed4 C.5.2, pp.C-64 to C-65].

---

## 7. Channel access on a shared frequency

### 7.1 Annex K as written

- States: OFFLINE, SENSE, LBT_WAIT, CONT_WAIT, LINKING [Ed4 K.2.2.8, Figure K-1, pp.K-3, K-7].
- Busy means the modem's data carrier detect (DCD), or a "virtual DCD" predicted from received EOT fields [Ed4 K.2.2.4, K.2.2.5, pp.K-2 to K-3].
- A node must never transmit before another node's EOT end time [Ed4 C.7.5.1.1, p.C-110; C.7.5.4.3, p.C-114].
- Defaults: CONT_SLOT_WIDTH 3 s, NUM_CONT_SLOTS 16, LBT_WAIT_TIMER_VALUE_DCD 30 s, LBT_WAIT_TIMER_VALUE_VDCD 3 s [Ed4 K.4 Table 7, p.K-12].
- Contention wait is either random jitter over the slots, or slotted with a per-node NODE_SLOT_POSITION [Ed4 K.3.2.3, pp.K-9 to K-10].
- With only one active link, between this node and one other, the LBT wait is zero [Ed4 K.3.2.2, p.K-9]. The contention wait that follows is not waived (section 4.1).
- Options and timers must be the same on all nodes [Ed4 K.1, p.K-1].

### 7.2 Our settings

- **Jitter or slotted is operator-selectable (D7)**, a network-wide setting that must match on every node [Ed4 K.1, p.K-1]. Slots are counted from the end of the last transmission heard [Ed4 K.3.2.3, p.K-10], so no shared clock or GPS is needed. The trade-off, to be measured in simulation before a default is chosen:
  - Jitter needs no coordination, but as written it adds a random 0 to 45 s contention wait before every transmission with the default settings, even between two nodes [Ed4 K.3.2.3, p.K-10]. That is too slow for ARQ turnarounds.
  - Slotted gives the node in slot 1 a zero wait and each later slot one more slot width [Ed4 K.3.2.3, p.K-10]. But each node needs a different slot position [Ed4 K.2.2.10, p.K-4], which needs coordinated configuration across the network.
  - Shrinking NUM_CONT_SLOTS or CONT_SLOT_WIDTH is allowed, but it must be the same on every node [Ed4 K.1, p.K-1]. The slot width must still exceed the sensing delay [Ed4 K.2.3, p.K-5].
  - Whichever we choose, T3 and T4 simulations measure the turnaround cost before any on-air work.
- **The 3 s, 16, 30 s and 3 s defaults are the starting point.** CONT_SLOT_WIDTH must exceed the total channel-sensing delay [Ed4 K.2.3, p.K-5]. For us that is the radio's transmit-to-receive turnaround, plus the preamble, plus audio latency. The preamble is about 0.72 s with our modem's default of M = 3 super-frames and no TLC: each super-frame is (9+4+5) x 32 symbols at 2400 symbols/s = 0.24 s [110D D.5.2.1, Table D-XIII, p.165; Ms110dTxSettings.cs]. 110D sets no default M. Measure on the bench before changing.
- **DCD = demodulator carrier detect.** It is true from preamble detection to burst end. Annex K expects DCD on "declaration of valid data", some interleaver frames later [Ed4 K.2.3, p.K-5]. Ours is declared earlier, which is more cautious and therefore acceptable.
- **Two-node condition (a)** "shall be checked" and condition (b) may be [Ed4 K.3.2.2, p.K-9]. We check (a) only.
- **End time from several EOTs:** receivers combine every EOT they parse to estimate the end time as accurately as possible, as Annex C requires [Ed4 C.7.5.1.1, p.C-110].
- **Jitter random numbers** come from a specified generator: an integer uniform over 0 to NUM_CONT_SLOTS-1. Annex K gives only the range [K.3.2.3]. The generator is written into the conformance spec so C# and C++ draw identical sequences in tests.

### 7.3 Optional extension: an external busy input

Annex K considers only 5066 signals [Ed4 K, whole annex; NOT STATED for other users]. Some channels are shared with other users or waveforms. For those, the station layer accepts an optional external busy signal, for example from an energy detector, and ORs it into DCD. It is off by default.

Annex K says any form of listen-before-transmit used with STANAG 5066 "shall conform to Annex K" [Ed4 K.1, p.K-1]. Feeding the signal into Annex K's own DCD input does that. It only ever makes the node more cautious and never causes a transmission the standard would forbid. It is documented as an extension, not as part of 5066.

---

## 8. Addressing

### 8.1 What the standard fixes

- Unicast node addresses are binary, 4 to 28 bits, written w.x.y.z with w in 0-15 (0.0.0.0 to 15.255.255.255) [Ed4 C.4.2.6, p.C-15].
- Group addresses add a flag treated as a 29th bit (16.0.0.0 to 31.255.255.255). Broadcast is 31.255.255.255 [Ed4 C.4.2.6, p.C-15].
- Group addresses may be used only as the destination of non-ARQ traffic [Ed4 S.5.16.1, p.S-19].
- In S_primitives an address is 4 bytes: 3 bits of size in half-bytes, the group flag, then 28 address bits, right-aligned [Ed4 S.5.16.1, Fig S-19, pp.S-18 to S-19].
- On air, leading zero bytes may be dropped, so small addresses cost less header space [Ed4 C.4.2.6, pp.C-15 to C-16].
- Annex N's regional and national address blocks are **informative** [Ed4 N, p.N-1]. Addresses with w = 0 are "ad hoc" and unmanaged, and blocks 14 and 15 are "other, unassigned" [Ed4 N.3, Table N-1, pp.N-1 to N-2; Table N-11, p.N-8].
- No primitive carries the local node address. S_BIND_ACCEPTED returns only SAP ID and MTU [Ed4 A.2.2.3, p.A-6]. NOT STATED anywhere, found by checking every primitive in A.2.2 and S.5. The node address is configuration.

### 8.2 Our approach

- A node's address is configuration: one 5066 node address, written w.x.y.z. Peers are addressed by their 5066 addresses, exactly as the standard defines. The node maintains no name directory.
- Group memberships are configuration too [Ed4 A.2.2.9, p.A-12].
- Which address block a deployment uses is the deployment's choice. Annex N gives informative guidance [Ed4 N, pp.N-1 to N-8].
- **Mapping names or callsigns to addresses is external.** To start with it is manual: a table kept by the operator or the application. Any automatic scheme later is an ordinary SIS client or a library above the node, not part of it. Options already explored, such as FT8-style packing and the CS38/1 codec, are kept in `notes/` for that layer.

---

## 9. Optional station-layer hooks

These sit below the 5066 interface, are off by default, and change no 5066 PDU.

### 9.1 Identification hooks (for example a CW identifier)

Some deployments must identify themselves in a form outsiders can read. Amateur use in the UK, for example, needs the call sign "transmitted as frequently as is practicable during transmissions", in "voice or other appropriate format" [OFW611 clause 21]. The station layer therefore offers identification hooks. The deployment configures what they send, such as a CW identifier.

Decision D4: two hooks, nothing at the end of transmissions.

- **Pre-session hook:** runs before the node's first transmission after it has been idle, for example a CW identifier.
- **Periodic hook:** runs on a configured interval (for example 15 minutes), but only if the node has transmitted since the last time it ran, so an idle node stays silent.
- **Each hook output is a transmission of its own.** It is queued like any other and obeys Annex K channel access. It is never sent while another node's EOT says that node is still transmitting [Ed4 C.7.5.4.3, p.C-114].
- **Tone:** a keyed tone at the modem's audio centre (1800 Hz at 3 kHz [110D Table D-I, p.143]), inside the same passband, with shaped keying.

### 9.2 External busy input

Section 7.3.

---

## 10. The application interface, and how users and developers use it

### 10.1 The interface: SIS over TCP, exactly as Annex S defines it

- TCP port 5066, configurable. The node accepts 16 simultaneous connections, one client per SAP [Ed4 S.2, p.S-3; S.1, p.S-2].
- Framing per primitive: `90 EB`, version `00`, 2-byte big-endian size, then the type byte and body [Ed4 S.5.2, pp.S-8 to S-10].
- Primitives: bind, unbind, bind accepted or rejected, unbind indication, unidata request, indication, confirm and reject, flow on and off, keep-alive, subnet availability, management message [Ed4 S.5.3 to S.5.15]. Type codes are in `notes/sis-addressing.md` 1.2.
- Bind carries SAP ID and default service type: ARQ or non-ARQ, delivery confirmation, delivery order, and a minimum retransmission count for non-ARQ. Unidata carries priority 0-15 (15 highest), destination SAP and address, delivery mode and TTL (20 bits, 2 s units, maximum 648000; for TTL 0 see section 4.4) [Ed4 S.5.12, Fig S-15, p.S-15; A.2.2.6, pp.A-7 to A-9].
- Bind accepted returns MTU = 2048 [Ed4 A.2.2.3, p.A-6].
- SAP assignments follow Annex F: 0 subnet management, 1 COSS, 2 ACP 142, 3 HMTP, 4 HFPOP, 5 chat, 6 XMPP, 7 ACP 142 email, 8 reserved, 9 IP client, 12 CFTP, and 10, 11, 13, 14, 15 unallocated [Ed4 F Table F-1, p.F-1]. New applications take unallocated SAPs first [Ed4 F.2, p.F-2].
- **Node information** that no core primitive provides (own address, current rate and SNR per peer) is offered through S_MANAGEMENT_MSG. Annex S makes its message type and body implementation-dependent [Ed4 S.5.11, p.S-15]. We publish our encoding in the project documentation, `docs/management-messages.md` (MSG TYPE 1, the MAC-mode report, since P5-3).
- There is no authentication on the SIS socket [NOT STATED in Ed4 S]. The host binds to localhost by default. Remote clients come in through an SSH tunnel, or by an explicit listen address.

### 10.2 Client library (C#)

The library implements every client duty from Annexes A and S, so application authors cannot get them wrong:

- Bind first. Unbind and rebind to change the default service type [A.2.2.1; S.5.3].
- Respect the MTU. Never request in-order delivery for non-ARQ. Use group addresses only for non-ARQ [A.2.2.6, p.A-8; S.5.16.1].
- Stop sending on FLOW_OFF. Always keep reading the socket, because a client must never flow-control the node [S.4.1, p.S-5].
- Answer keep-alive within 10 s, and send one no more often than every 120 s [S.4.4, p.S-6].
- Bind with a valid service type: Transmission Mode not 0, delivery confirmation only with ARQ, Extended Field 0, RANK 0 [S.5.3, pp.S-11 to S-12; A.2.2.6, p.A-8].
- If a keep-alive reply cannot be sent within 10 s, send none [S.4.4, p.S-6].
- Library policy (not a duty in the standard): after an unbind indication, rebind automatically, except after reason 5 "ARQ Mode Unsupportable during Broadcast" [A.2.2.5, p.A-7].
- **Confirmation matching:** confirms and rejects carry no request ID, only the destination and a possibly truncated copy of the U_PDU [A.2.2.7, p.A-10]. The helper therefore matches on destination, SAP and the U_PDU prefix, and documents that applications wanting exact matching should start each U_PDU with their own message ID.
- Optional helper: decoding our S_MANAGEMENT_MSG node information.

API shape, in idiomatic async C#:

```csharp
await using var sis = await SisClient.ConnectAsync("localhost", 5066);
var bind = await sis.BindAsync(sap: 13, ServiceType.Arq(confirm: Confirm.Node));
await sis.SendAsync(Address.Parse("0.0.0.5"), destSap: 13, payload,
                    priority: 6, ttl: TimeSpan.FromMinutes(30));
await foreach (var msg in sis.ReceiveAllAsync()) { ... }   // indications
sis.Confirmations / sis.Rejections                         // observable streams
```

### 10.3 What users see

- A daemon, installed from the packet-net apt repo, configured with its 5066 node address, the radio (audio device, PTT or CAT) and the network's CSMA settings, plus any optional hooks the deployment needs.
- A status page showing heard nodes by address, the current speed and interleaver per peer, SNR and queue depth.
- Applications are separate programs that connect to it. The first one shipped is an HF chat client (Annex O), so two stations can prove the link with nothing else installed.

### 10.4 External adapters, all outside the interface

Each of these is an ordinary SIS client, bound to its own SAP:

- **Name or callsign mapping:** manual tables first. Any automatic scheme comes later, as a client or library.
- **Mail and files:** CFTP (Annex V) as specified, then bridges, for example to a BBS.
- **One-to-many bulletins:** non-ARQ group delivery with repeats [Ed4 C.5.2, pp.C-61 to C-64].
- **IP:** the Annex U IP client.

---

## 11. Test design, including the C++ port

### 11.1 Layers of testing

| Layer | What | Shared with C++? |
|---|---|---|
| T1 Codec vectors | Every PDU and field encoding: D_PDU types 0-15 including EOW and EOT, C_PDU 0-6, S_PDU, S_primitive framing, address encodings, speed and interleaver byte, both CRCs | Yes, byte-exact |
| T2 Scenario traces | Scripted inputs with timestamps (bytes received, modem events, SIS requests, clock ticks) and expected outputs (bytes transmitted, timers, SIS indications) for one node's core | Yes, byte-exact |
| T3 Multi-node simulation | Two or three cores over a simulated channel with seeded loss and delay. Assertions on delivery, ordering and absence of protocol violations. | Yes, same seeds and same outcome |
| T4 Audio-level simulation | Full stack through M0LTE.Ms110d over the HF channel simulator (HfChannel, net-sim). Throughput and turnaround measurements. | Statistical, not byte-exact |
| T5 Interop | C# node and C++ node talking over loopback audio, then on air. The 5066 test client drives both through the SIS. | Yes |
| T6 On air | GB7RDG and SA6DAZ path, receive side first | n/a |

### 11.2 Conformance matrix

One row per requirement we implement. Columns:

- ID;
- source (annex, section, page);
- a one-line paraphrase (never the NATO text);
- applies (yes, no, or n/a and why);
- implementation location;
- test IDs.

Ambiguity resolutions (section 4.4) are rows of their own. This plays the role of the JITC cross-reference table, which maps over 1000 requirement numbers to tests [JITC TP Appendix B, Table B-1, pp.B-3 onwards]. The matrix is a plain-text file in `s5066-conformance`, and CI fails if a row marked "applies" has no test.

### 11.3 The language-neutral corpus

- Format: JSON Lines, hex strings for bytes, integers for times in milliseconds, and no floating point in byte-exact tests.
- The corpus is versioned and lives in its own repository. Both implementations pull it in as a submodule or package, so neither can fork it silently.
- A tiny harness contract per language: read a case, feed the core, compare the outputs. The C++ port's first milestone is "all T1 and T2 cases pass", which can be run before any radio exists.
- The specified pseudo-random generator (section 7.2), the timer resolution and the ordering of simultaneous events are part of the corpus specification, so traces are deterministic in both languages.

### 11.4 Seed vectors already available and verified

- **Annex C CRC examples.** The WARNING D_PDU `90 EB F0 00 00 47 05 64 02 5F 1E` gives header CRC 0x1E5F. Bytes `F0 00 00 47 05 64 02` give CRC-32 0xF4178F95 [Ed4 C.4.2.8, C.4.2.11, pp.C-18 to C-22]. Both were recomputed and confirmed.
- **JITC TP injected D_PDUs.** These are a link request in a type 8 D_PDU, four type 0 data D_PDUs with real HMTP text, type 3 resets, and an Ed1 undefined type 10 [JITC TP pp.48 to 54, 132 to 136]. Every header and data CRC was recomputed and checks.
  - Caution: under Edition 4, type 10 is ACK-ONLY [Ed4 Table C-2], so the JITC "type 10 draws a WARNING" expectation is Ed1-only. Our test re-labels it as an Ed3-peer case.
  - The encoding tables for address size, header size, SAP, priority and destination encodings [JITC TP Tables 10.12 to 10.14, 11.6] become T1 cases.
- Note that the JITC TP's Annex D tests assume a synchronous serial modem, which we do not use [JITC TP refs 983 to 989]. Those rows are "n/a" in the matrix, with the reason.

### 11.5 Wireshark as an independent decoder

Wireshark has a STANAG 5066 DTS dissector (wiki.wireshark.org/STANAG_5066_DTS, read 2026-11-07). The wiki does not say which edition it follows. It says only that the DTS layer is the same in Editions 1 and 2, and that Edition 3 is backward compatible. Its Ed4 coverage is unverified. The host writes optional pcap captures of transmitted and received D_PDUs. Decoding Ed3-family traffic with an independent tool is a cheap cross-check. We expect it not to understand the Ed4 types 9 to 14. Check this in phase 1.

### 11.6 The test bed: a dedicated 5066 test client

A purpose-built tool, `s5066-test` (working name), drives and observes nodes. It follows pdn-qso's proven ideas: chat, file and performance activities, delivery and round-trip reporting, a headless mode for scripted runs, and a terminal UI that works over ssh. But it is written for 5066 alone and shares no AX.25 or IL2P code. It has two parts.

1. **SIS client and test driver.** It connects to a node over TCP 5066 exactly like any application, using only `S5066.Client`.
   - **Chat:** HF Operator Chat (Annex O, SAP 5).
   - **File:** CFTP (Annex V, SAP 12) once it exists, plain ARQ unidata before then.
   - **Perf:** scripted unidata streams with chosen priority, delivery mode, size and rate. It reports delivery confirmations, rejections with their reasons, goodput, latency and queue behaviour under flow control.
   - **Scenario mode:** runs a script against one or more nodes headless and writes a machine-readable result, so T4 simulations and on-air trials use the same harness.
   - **Node view:** shows the node information we publish through S_MANAGEMENT_MSG (section 10.1): the current speed, interleaver and SNR per peer.

   Because it is written against the SIS alone, it is also the first proof that the interface is enough for a real application.

2. **Monitor.** A receive-only decoder: audio in, through M0LTE.Ms110d and the `S5066.Core` codecs. It shows every D_PDU heard, with type, addresses, sequence numbers, EOT, the EOW rate recommendations, the burst's waveform and interleaver, and SNR. It can also write pcap files for Wireshark (section 11.5). It runs from any receive-only audio source, for example an UberSDR receiver, so it gives an independent view of what actually went over the air.

Radio and audio device support (Flex, sound card plus PTT, UberSDR) comes from the station layer chosen in D3. If pdn-qso's device code is the best existing implementation, it should be moved into a shared package, not copied.

The work slots into phases 6 (SIS client part), 8 (monitor) and 9 (on-air trials). Decision D8.

---

## 12. Implementation phases

The authoritative work breakdown for phases 3 to 7 is in `docs/core-architecture.md` section 9, which moves CSMA and a thin SIS/CAS slice into phase 3, CAS-1 linking into phase 4, and block alignment and PADDING into phase 4 (they need a peer known to be Ed4). The table below is the original outline.

Each phase ends with its tests green, its conformance matrix rows filled, and a short demo. Work runs on branches with draft PRs pushed early, so it can be handed over.

| Phase | Deliverable | Done when |
|---|---|---|
| 0. Foundations | Decisions D1 to D8 settled. Repos created. Conformance matrix skeleton for Annexes A, B, C, J, K, S. Corpus format and harness contract written. | Matrix lists every "shall" in scope with an applies or n/a verdict |
| 1. Codecs | CRC-16 and CRC-32, D_PDU (all 16 types), EOW (all types), C_PDU 0-6, S_PDU, Annex S framing, address and speed/interleaver bytes | All T1 vectors pass, including the Annex C examples and JITC vectors |
| 2. Modem work | M0LTE.Ms110d: SNR estimate, C# burst timestamps, run-time max blocks, `PlanBurst`, in C# and C++. Modem port in S5066.Station. | SNR estimate within an agreed error of the simulator's true SNR over the working range; same events from C# and C++ on the same audio |
| 3. DTS, non-ARQ | Types 7 and 8, segmentation and reassembly, reception window, repeats, EOT generation and parsing (end time from several EOTs), PADDING, block alignment, MANAGEMENT type 6 stop-and-wait, transmission order rules and TTL expiry handling [Ed4 C.7.5.2, pp.C-110 to C-112] | T2 traces; T3 broadcast to two receivers with loss |
| 4. DTS, ARQ | Ed4 family 9 to 12 with bitmap and RLE acknowledgements, windows to 32,768, the sync tests (equations 1 to 5), RESET/WIN-RESYNC and FULL RESET, then the Ed3 family 0 to 3, the WARNING rules, and rejecting unprocessed C_PDUs when a link ends [Ed4 C.7.5.2.3, p.C-112] | T2 traces for each state table row in scope; T3 with loss, duplication and reordering |
| 5. CAS and MAC | CAS-1 link make, accept, reject, break and clean close with Ed4 and Short negotiation, and Ed3 fallback when the Ed4 bit draws a WARNING [Ed4 B.7.2.2, p.B-31]. "Address Not Known" rejection [Ed4 B.7, p.B-12]. Annex K CSMA with EOT-based VDCD, in the mode chosen in D7. Annex J channel analysis for protocol violations [Ed4 J.4.1.2, p.J-7]. The optional external busy input. | Three-node T3: two links plus broadcast, no transmission ever overlaps an announced EOT |
| 6. SIS and host | The SIS client part of the 5066 test client (section 11.6). SIS service and Annex S server on TCP 5066, soft link session management with its minimum hold time and break reasons [Ed4 A.3.2.1, pp.A-19 to A-21], client-to-client delivery confirmation S_PDUs and their timers [Ed4 A.3.1.2, A.3.1.3, A.3.2.3, pp.A-17 to A-24], client library, S_MANAGEMENT_MSG node info, HF chat sample app | Two hosts over T4 audio simulation exchange chat both ways; client library passes its own duty tests |
| 7. Rate selection | EOWs 8 to 12 and 15 sent and acted on, ED3-BASIC-RATE to Ed3 peers, the algorithm in section 6.2, calibrated by T4 sweeps over AWGN and Poor channels | Throughput versus SNR curve published, beating a fixed-rate baseline at every SNR tested |
| 8. Station and packaging | The 5066 test client's monitor (section 11.6). PTT/CAT, status page, apt package, user docs (short, friendly), the optional identification hooks and the external busy input | Bench run on radio1 and radio2 with a real radio path; a configured CW ID decodable by ear and by a CW decoder |
| 9. On air | GB7RDG and SA6DAZ trials, receivers started first, measurements logged, the 5066 test client as the operator interface and its monitor as the independent view | A file and a chat exchange both ways on 40 m. Results written up. |
| 10. Applications | CFTP (Annex V), then the IP client and external bridges | Each passes its annex's tests and runs over the air |
| C++ port | Starts once phases 1 to 5 are stable. Driven entirely by the corpus. | All T1 to T3 cases pass, and C# and C++ interoperate in T5 |

---

## 13. Decisions (settled 2026-11-07)

| ID | Decision |
|---|---|
| D1 | Build from the Isode-hosted Ed4 annexes, citing and paraphrasing only, no NATO text in the repos. Tom asks around for: (1) the latest AComP-5066 Edition A (we have Version 1; a search result mentioned a Version 2 of May 2024, unconfirmed); (2) STANAG 5069 or AComP-5069, to confirm waveform numbering; (3) from Isode, an evaluation copy of their 5066 server for interoperability testing, plus the S5066-EP4, EP5 and EP6 papers. |
| D2 | Owner M0LTE. Licence AGPL-3.0-or-later plus commercial. Names: `M0LTE.S5066.*` packages; repos M0LTE/M0LTE.S5066 and M0LTE/s5066-conformance; test client `s5066-test`. |
| D3 | Host inside pdn-soundmodem first, as the ARDOP work did: the engine is its own package, and pdn-soundmodem adds a 5066 host server beside the KISS server, an audio bridge and busy detection. No AX.25 or amateur baggage in the 5066 path. |
| D3 amendment (2026-11-08) | Tom's decision: a standalone `s5066d` per machine drives a CM108 audio interface and the radio directly, with no pdn-soundmodem daemon, configuration page or KISS server. It uses the published pdn-soundmodem library package for the sound card, CM108 PTT and busy detection, as pdn-mailcast does, in its own small project (`M0LTE.S5066.Radio`) so that `M0LTE.S5066.Host` stays free of it (core-architecture.md 12.11). The pdn-soundmodem integration (pdn-soundmodem PR #784) stays as a second way to run a node. |
| D4 | Identification hooks: a pre-session hook and a periodic hook (section 9.1), intended for a CW ID. Nothing at the end of transmissions. |
| D5 | Withdrawn: callsign mapping is external. |
| D6 | Implement the minimum Ed3 interop. It is mandatory in Edition 4: Ed4 requires peer edition detection and Ed3 behaviour towards Ed3 peers [Ed4 main 6, p.10], and Isode's Edition 5 paper says Ed3 support is "no longer mandatory" only from Edition 5 (p.2). |
| D7 | CSMA mode is operator-selectable (jitter, slotted, or ed3-compatible), documented as a network-wide setting. No GPS needed. The default is chosen after T3/T4 measurement. |
| D8 | Build the dedicated test client `s5066-test` (section 11.6). |

---

## 14. Risks and known gaps in the standards

| Ref | Item | Effect | Mitigation |
|---|---|---|---|
| A1 | STANAG 5069 waveform numbers 1-13 equal 110D Appendix D WN 1-13 | Resolved 2026-11-07 | Confirmed against AComP-5069 Edition A Version 1 (Table B.3.1.2-1 and B.3.2.1.3.2), obtained by Tom. It is held locally only, never in a repo. |
| A2 | Ed3 text unavailable. Ed4 refers out to it for hard links, expedited primitives, HDRCR, Ed3 DRC and Ed3 Annex I | None for our scope, because all are out of scope (section 4.3) | Note in the matrix as n/a with reason |
| A3 | Several Ed4 inconsistencies (section 4.4) | Possible mismatch with other implementations | Each resolution is a matrix row. Revisit if an official Ed4 copy differs. |
| A4 | No timer values for CAS link setup, link break, ACK wait or confirmation wait [Ed4 B.7.2.1, B.7.2.8; C.7.5.6; A.3.2.3] | Tuning burden | Derive from burst duration plus turnaround, measured in T4, and document the formulas |
| A5 | The modem SNR estimator is new work | Rate selection quality depends on it | Phase 2 validation against the simulator, before phase 7 |
| A6 | CSMA defaults (3 s slots, 30 s DCD wait) may be slow for ARQ turnarounds | Throughput | Measure first; any change must be network-wide [Ed4 K.1] |
| A7 | False positives from an external busy detector, where one is used | Missed turns | Off by default; threshold relative to the measured noise floor |
| A9 | The JITC TP covers Edition 1 only | Partial conformance evidence | Our own matrix covers Ed4. The JITC cases are reused where they still apply. |

---

## 15. Project organisation

- **Durable state:** GitHub M0LTE/M0LTE.S5066 holds the plan (`docs/plan.md`), the live status (`docs/status.md`) and the tracking issues: one per phase, plus one per work item. Nothing important lives only in a chat session.
- **Coordinator:** one Claude session splits phases into PR-sized items and hands them to sub-agents in git worktrees. It writes no code itself.
- **Spec first:** phase 0 turns every "shall" into a conformance matrix row with its citation, reviewed against the annex text. Rows become corpus tests, and tests drive code.
- **Review:** one fresh reviewer per change, given the matrix rows and annex pages, not the author's summary.
- **Models:** Opus 5.5 for the coordinator, reviewers and protocol state machines. Sonnet 5.5 for mechanical code, tests and docs. Fable 5.1 for judgement calls only (ambiguity rulings, SNR estimator and rate algorithm design, contention-mode results). Haiku 5.5 for small transcription jobs, always reviewed.
- **Parallel workstreams:** modem changes (M0LTE.Ms110d C# and ms110d-cpp) run in their own session from phase 0. The C++ port of the stack runs in its own session after phase 5.
- **Modem consumers:** pdn-soundmodem and pdn-mailcast consume M0LTE.Ms110d. Every modem change is additive, versioned under its SemVer policy, and checked against both consumers before release.
- **Compute:** routine tests run on our own machines. Audio-level sweeps run on AWS spot instances requested from Tom, and only small compressed results come back.
- **Tom's involvement:** direct questions only when needed, by mobile notification. Check-ins at the end of phase 0, after SNR estimator validation, after the contention-mode measurements, and before anything goes on air.
