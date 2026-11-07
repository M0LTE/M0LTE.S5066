# S_MANAGEMENT_MSG messages

Annex S leaves the MSG TYPE and MSG BODY of S_MANAGEMENT_MSG_REQUEST and S_MANAGEMENT_MSG_INDICATION to the implementation [Ed4 S.5.11, p.S-15]. This page is our encoding, for client authors (plan 10.1). The node sends these to every client bound to a SAP, in connection order, without being asked. Unbound connections get nothing.

A client should ignore any MSG TYPE, or any finding code, it does not know. More message types will be added (node information, P6-2).

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
