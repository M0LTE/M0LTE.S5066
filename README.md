# M0LTE.S5066

A STANAG 5066 Edition 4 HF data link (Subnetwork Interface, Channel Access, Data Transfer and CSMA sublayers) running over the MIL-STD-188-110D Appendix D modem in [M0LTE.Ms110d](https://github.com/M0LTE/M0LTE.Ms110d). Applications use the standard SIS interface on TCP port 5066.

Status: early development. The plan is [docs/plan.md](docs/plan.md), and progress is tracked in [docs/status.md](docs/status.md) and the issues.

The conformance corpus and requirements matrix live in [M0LTE/s5066-conformance](https://github.com/M0LTE/s5066-conformance).

## Building

Clone with submodules (`git clone --recurse-submodules`, or `git submodule update --init` afterwards), then `dotnet build` and `dotnet run --project tests/M0LTE.S5066.Core.Tests`. The tests run the shared corpus in `conformance/`.

## Licence

AGPL-3.0-or-later; see [LICENSE](LICENSE). If the AGPL does not suit your use, commercial licences are available from the copyright holder, Thomas Fanning. To ask, open a GitHub issue titled "Commercial licence enquiry". Because of that, outside contributions can only be accepted with a contributor licence agreement; please open an issue before writing code.

The commercial offer covers the libraries M0LTE.S5066.Core, M0LTE.S5066.Host, M0LTE.S5066.Client and M0LTE.S5066.Cftp (mail over CFTP, [docs/cftp.md](docs/cftp.md)), which stay dual licensed. It does not cover `s5066d` or `tools/s5066-test` (whose monitor captures from a sound card) as built here, nor `src/M0LTE.S5066.Radio`, its sound card and PTT support: that project links the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) package, which is AGPL-3.0-or-later AND GPL-3.0-or-later AND GPL-3.0-only, so `s5066d` and `s5066-test` are distributed under those terms combined (AGPL-3.0-or-later AND GPL-3.0-or-later AND GPL-3.0-only) and only under them. The radio project is not packed, and no packed library references it.
