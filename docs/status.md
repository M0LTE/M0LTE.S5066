# Status

Last updated: 2026-11-07.

## Now

Phase 0 is complete. Phase 1 (codecs) is in progress: P1a (scaffold, corpus harness, CRC-16, CRC-32, SplitMix64), P1c (C_PDU and S_PDU codecs) and P1d (SIS S_primitive codec, `src/M0LTE.S5066.Core/Sis/`) are merged; P1b (D_PDU, EOW and address codecs, issue #11) is in review. Phase 2 (modem work, design in progress as M0LTE/M0LTE.Ms110d draft PR) runs alongside.

## Phases

| Phase | State |
|---|---|
| 0. Foundations: requirements matrix, corpus format, harness contract | done |
| 1. Codecs | in progress: P1a (#10), P1c (#12), P1d (#13) done; P1b in review (#11) |
| 2. Modem work (M0LTE.Ms110d, ms110d-cpp) | design merged (Ms110d#19); 0.5.0 in progress (#8) |
| 3. DTS non-ARQ | not started |
| 4. DTS ARQ | not started |
| 5. CAS and MAC | not started |
| 6. SIS and host | not started |
| 7. Rate selection | not started |
| 8. Station and packaging | not started |
| 9. On air | not started |
| 10. Applications | not started |
| C++ port | not started |

## Phase 0 outcome

- Requirements matrix in M0LTE/s5066-conformance `matrix/`: 1,980 rows (1,101 shall, 59 should, 107 may, 559 info, 154 interp); 1,704 apply, 268 do not, 8 not applicable. Every file was independently reviewed against the annex text (s5066-conformance PRs #1, #2, #4, #5, #6).
- Corpus format, harness contract and SplitMix64 PRNG spec in `docs/`; 27 seed T1 cases; validator in `tools/` (PR #3).
- Coordinator rulings are recorded in the matrix rows themselves (search notes for "Coordinator ruling") and in issue #9.

## Phase 1 outcome

- Core codecs for CRCs, PRNG, every D_PDU type and EOW, addresses, C_PDUs, S_PDUs and the Annex S primitives, all byte-exact against the corpus (corpus v0.2.0, over 600 cases; M0LTE.S5066 PRs #14 to #17).
- Open for interop confirmation: 16-bit FSN/LWE byte order (C-0134).

## Open questions for Tom

None.

## Log

- 2026-11-07: repos created (public). Plan v1.1 committed with decisions D1 to D8.
- 2026-11-07: phase 0 complete; plan v1.2.
- 2026-11-07: phase 1 complete; corpus v0.2.0.
- 2026-11-07: P1a done (issue #10): solution, M0LTE.S5066.Core, conformance submodule at corpus 0.1.0, corpus harness (CRC and PRNG cases pass; D_PDU and T2 cases skipped until P1b and phase 3), CI.
- 2026-11-07: P1c (issue #12): C_PDU (Annex B types 0 to 10, 7 to 10 decode only) and S_PDU (Annex A types 0 to 2) codecs in Core, 89 T1 cases from conformance PR #7; all pass.
- 2026-11-07: P1b (issue #11): codecs for D_PDU types 0 to 15, EOWs, address field and speed byte in `M0LTE.S5066.Core.Dts`; 403 new T1 vectors (corpus 0.2.0); all corpus cases pass. Review: type 13 decodes as an unrecognised extension (C-0365), the D_PDU codec shares PduErrorKind with P1c.
