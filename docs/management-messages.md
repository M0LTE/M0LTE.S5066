# S_MANAGEMENT_MSG messages

Annex S leaves the MSG TYPE and MSG BODY of S_MANAGEMENT_MSG_REQUEST and S_MANAGEMENT_MSG_INDICATION to the implementation [Ed4 S.5.11, p.S-15]. This page is our encoding, for client authors (plan 10.1).

| MSG TYPE | Sent | Contents |
|---|---|---|
| 1 | Unprompted, to every client bound to a SAP, in connection order. Unbound connections get nothing. | Another node's MAC mode |
| 2 | Only in answer to a request of MSG TYPE 2, to the connection that asked, bound or not | Node information |

A client should ignore any MSG TYPE, or any code, it does not know, and any bytes after the ones described here. The node ignores a request with any other MSG TYPE, sends no reply, and does not count it as an invalid primitive.

## MSG TYPE 1: another node's MAC mode

The node watches the channel for signs that other nodes run a different medium access mode from ours (CSMA), as Annex J asks [Ed4 J.4.1.2, pp.J-7 to J-8], and tells clients when what it knows about a node changes. Matrix rows J-0051, J-0082 and J-0083.

The S_PDU is primitive type 19, then MSG TYPE 1, then a 9-byte MSG BODY:

| Octets | Field |
|---|---|
| 0 | Finding code, below |
| 1 to 4 | The node the finding is about |
| 5 to 8 | For codes 3 and 4, the node the WARNING was addressed to; for codes 1 and 2, all zero |

Each address is a 4-byte S_primitive address field, the same as in S_UNIDATA_INDICATION [Ed4 S.5.16.1, Fig S-19]: SIZE 7 and the group bit clear, so the first byte is `0xE0` plus the top 4 address bits. All four bytes zero means no address.

| Code | Meaning |
|---|---|
| 1 | The node is using WTRP (Annex L): we heard a WTRP D_PDU or a WTRP token from it. |
| 2 | The node, last reported as code 1, sent a transmission with no WTRP in it, so it is now in P2P or CSMA mode. |
| 3 | The node sent a WARNING saying it does not recognise a WTRP D_PDU (type 13, reason 0 or 4): it can run only P2P or CSMA, probably an older implementation. |
| 4 | The node sent a WARNING saying a WTRP D_PDU was unexpected (type 13, reason 3): it runs a different MAC mode from the node it warned. |

When reports are sent: at the end of each received burst. Codes 1 and 2 only when a node's mode changes (every node starts presumed P2P or CSMA, so code 2 only ever follows a code 1). Codes 3 and 4 unless the finding, addressee included, is the same as the last one reported for that node. Several in one burst come mode findings first, by node address, then WARNING findings in the order they were received.

Example: `90 EB 00 00 0B 13 01 01 E0 00 01 03 00 00 00 00` reports that node 0.0.1.3 is using WTRP.

The node never changes its own MAC mode or channel because of these findings.

## MSG TYPE 2: node information

Ask with S_MANAGEMENT_MSG_REQUEST, MSG TYPE 2. The node ignores the request's MSG BODY, so send none. It answers at once with S_MANAGEMENT_MSG_INDICATION, MSG TYPE 2, to the connection that asked, bound or not. Matrix rows S-0038 and S-0164.

The MSG BODY is 7 bytes, then one 13-byte record per peer:

| Octets | Field |
|---|---|
| 0 to 3 | This node's address |
| 4 | The waveform number the node transmits with |
| 5 | The interleaver it transmits with, coded below |
| 6 | N, the number of peer records that follow (at most 255) |
| 7 onward | N peer records, in address order |

Each peer record:

| Octets | Field |
|---|---|
| 0 to 3 | The peer's address |
| 4 | Soft link to it: 0 none, 1 being made or held off, 2 established, 3 closing |
| 5 | The waveform number the node transmits to it with |
| 6 | The interleaver it transmits to it with |
| 7 | The waveform number of the last burst heard from it, 255 if never heard |
| 8 | The interleaver of that burst, 255 if never heard |
| 9 and 10 | That burst's SNR in tenths of a dB, signed 16-bit, high byte first; `80 00` when the modem gave none or the peer was never heard |
| 11 and 12 | Whole seconds since that burst ended, high byte first, at most 65534; `FF FF` if never heard |

Addresses are 4-byte S_primitive address fields, as in MSG TYPE 1. Interleaver codes: 0 ultra short, 1 very short, 2 short, 3 medium, 4 long, 5 very long.

The peers listed are those the node has heard a D_PDU from, whoever it was addressed to, and those it has a soft link record for (a client sent ARQ data to them). If there are more than 255, the 255 lowest addresses are listed. The node sends each peer at the same speed for now; the transmit fields are per peer because the node will choose a speed per peer later (phase 7).

Example: `90 EB 00 00 30 13 02 E0 00 01 02 02 04 03 E0 00 01 03 00 02 04 02 04 00 7B 00 04 E0 00 01 05 00 02 04 03 02 FF C7 00 01 E0 00 01 06 01 02 04 FF FF 80 00 FF FF` is from node 0.0.1.2, which transmits at waveform 2, long interleaver. It heard 0.0.1.3 4 seconds ago at waveform 2 long, SNR 12.3 dB, and 0.0.1.5 1 second ago at waveform 3 short, SNR -5.7 dB, and is making a soft link to 0.0.1.6, never heard.
