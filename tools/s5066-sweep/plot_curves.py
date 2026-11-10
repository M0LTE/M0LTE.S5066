#!/usr/bin/env python3
"""Plot the P7-10 curve: goodput against set SNR, one image per channel (docs/rate-selection.md).

Reads a merged sweep's aggregate directory (curve-<channel>.tsv and points.tsv from
`s5066-sweep merge`) and writes curve-<channel>.svg into the output directory, with the
standard library only. When matplotlib can be imported it also writes curve-<channel>.png.

Each plot: the adaptive node's mean over the seeds (line) with its minimum to maximum (band),
the channel's baseline rung (open question 14) the same way, each point's best fixed rung
(the oracle, dashed, not gated), and every other fixed rung as a thin grey line.

usage: plot_curves.py <aggregate dir> [<output dir>]   (output defaults to the aggregate dir)
"""

import csv
import html
import os
import sys

BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"
INK, MUTED, GRID, SURFACE, OTHER = "#0b0b0b", "#52514e", "#e4e3df", "#fcfcfb", "#c3c2b7"
NAMES = {"awgn": "AWGN", "poor": "Poor (CCIR Poor, Watterson)", "poor-ssb": "Poor with SSB band-pass (400 to 2600 Hz)",
         "moderate": "Moderate", "moderate-ssb": "Moderate with SSB band-pass", "awgn-ssb": "AWGN with SSB band-pass"}


def rung(config):
    """fixed-wn8-long -> WN 8 Long; adaptive-ms110d-cs-0.6.1 -> adaptive (ms110d-cs-0.6.1)."""
    if config.startswith("fixed-wn"):
        wn, il = config[len("fixed-wn"):].split("-", 1)
        return "WN %s %s" % (wn, il.replace("_", " ").capitalize())
    if config.startswith("adaptive-"):
        return "adaptive (%s)" % config[len("adaptive-"):]
    return config


def read_tsv(path):
    with open(path, newline="", encoding="ascii") as f:
        return list(csv.DictReader(f, delimiter="\t"))


def load(agg):
    """Per channel: {'points': [...points.tsv rows...], 'fixed': {config: [(snr, mean)]}}."""
    channels = {}
    for row in read_tsv(os.path.join(agg, "points.tsv")):
        channels.setdefault(row["channel"], {"points": [], "fixed": {}})["points"].append(row)
    for ch, data in channels.items():
        path = os.path.join(agg, "curve-%s.tsv" % ch)
        if not os.path.exists(path):
            continue
        for row in read_tsv(path):
            if row["config"].startswith("fixed-"):
                data["fixed"].setdefault(row["config"], []).append(
                    (int(row["set_snr_tenth_db"]) / 10.0, float(row["goodput_mean_bps"])))
    for data in channels.values():
        data["points"].sort(key=lambda r: float(r["set_snr_db"]))
        for v in data["fixed"].values():
            v.sort()
    return channels


def num(s):
    return None if s in ("", "-") else float(s)


def series(points):
    """x, adaptive (mean, min, max), baseline (mean, min, max), best fixed mean, baseline config."""
    x = [float(r["set_snr_db"]) for r in points]
    a = [(num(r["adaptive_mean_bps"]), num(r["adaptive_min_bps"]), num(r["adaptive_max_bps"])) for r in points]
    b = [(num(r["baseline_mean_bps"]), num(r["baseline_min_bps"]), num(r["baseline_max_bps"])) for r in points]
    best = [num(r["best_fixed_mean_bps"]) for r in points]
    base = next((r["baseline_config"] for r in points if r["baseline_config"] != "-"), None)
    adaptive = points[0]["adaptive_config"] if points else "adaptive"
    return x, a, b, best, base, adaptive


def nice_top(v):
    for step in (100, 200, 250, 500, 1000):
        top = step * -(-v // step)
        if top / step <= 8:
            return int(top), step
    return int(1000 * -(-v // 1000)), 1000


def svg(channel, data):
    W, H, L, R, T, B = 760, 440, 72, 24, 56, 56
    x, a, b, best, base, adaptive = series(data["points"])
    ymax = max([v for t in a + b for v in t if v is not None] + [v for v in best if v is not None] + [1])
    top, step = nice_top(ymax * 1.05)
    x0, x1 = (min(x), max(x)) if x else (0, 30)
    x1 = x1 if x1 > x0 else x0 + 1

    def px(v):
        return L + (v - x0) / (x1 - x0) * (W - L - R)

    def py(v):
        return H - B - v / top * (H - T - B)

    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d" font-family="sans-serif" font-size="12">' % (W, H, W, H),
           '<rect width="100%%" height="100%%" fill="%s"/>' % SURFACE,
           '<text x="%d" y="22" font-size="15" font-weight="bold" fill="%s">%s: goodput against set SNR</text>' % (L, INK, html.escape(NAMES.get(channel, channel))),
           '<text x="%d" y="40" fill="%s">Mean over the seeds; bands from the lowest to the highest seed. Bulk ARQ, 2048-byte U_PDUs, 30 minute runs.</text>' % (L, MUTED)]
    for i in range(0, top + 1, step):
        out.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" stroke="%s" stroke-width="1"/>' % (L, W - R, py(i), py(i), GRID))
        out.append('<text x="%d" y="%.1f" text-anchor="end" fill="%s">%d</text>' % (L - 6, py(i) + 4, MUTED, i))
    for v in x:
        out.append('<text x="%.1f" y="%d" text-anchor="middle" fill="%s">%g</text>' % (px(v), H - B + 18, MUTED, v))
    out.append('<text x="%d" y="%d" text-anchor="middle" fill="%s">set SNR (dB in 3 kHz)</text>' % ((L + W - R) // 2, H - 14, INK))
    out.append('<text x="16" y="%d" text-anchor="middle" fill="%s" transform="rotate(-90 16 %d)">goodput (bit/s)</text>' % ((T + H - B) // 2, INK, (T + H - B) // 2))

    def path(pts):
        pts = [(u, v) for u, v in pts if v is not None]
        return "M" + " L".join("%.1f,%.1f" % (px(u), py(v)) for u, v in pts) if pts else ""

    for cfg, pts in sorted(data["fixed"].items()):
        if cfg == base:
            continue
        out.append('<path d="%s" fill="none" stroke="%s" stroke-width="1"><title>%s</title></path>' % (path(pts), OTHER, html.escape(rung(cfg))))

    def band(tr, colour):
        lo = [(u, t[1]) for u, t in zip(x, tr) if t[1] is not None]
        hi = [(u, t[2]) for u, t in zip(x, tr) if t[2] is not None]
        if lo and hi:
            d = path(hi) + " L" + " L".join("%.1f,%.1f" % (px(u), py(v)) for u, v in reversed(lo)) + " Z"
            out.append('<path d="%s" fill="%s" fill-opacity="0.15" stroke="none"/>' % (d, colour))

    band(b, ORANGE)
    band(a, BLUE)
    out.append('<path d="%s" fill="none" stroke="%s" stroke-width="2" stroke-dasharray="5 4"/>' % (path(list(zip(x, best))), AQUA))
    for tr, colour, name in ((b, ORANGE, rung(base) + ", the baseline" if base else "baseline"), (a, BLUE, rung(adaptive))):
        out.append('<path d="%s" fill="none" stroke="%s" stroke-width="2"/>' % (path([(u, t[0]) for u, t in zip(x, tr)]), colour))
        for u, t in zip(x, tr):
            if t[0] is not None:
                out.append('<circle cx="%.1f" cy="%.1f" r="4" fill="%s" stroke="%s" stroke-width="2"><title>%s, %g dB: %.0f bit/s (%.0f to %.0f)</title></circle>'
                           % (px(u), py(t[0]), colour, SURFACE, html.escape(name), u, t[0], t[1], t[2]))
    ly = T + 10
    out.append('<rect x="%d" y="%d" width="270" height="76" fill="%s" fill-opacity="0.85"/>' % (L + 6, ly - 10, SURFACE))
    for colour, dash, label in ((BLUE, "", rung(adaptive)), (ORANGE, "", (rung(base) + " fixed, the baseline") if base else "no baseline"),
                                (AQUA, ' stroke-dasharray="5 4"', "best fixed rung at each point"), (OTHER, "", "other fixed rungs (Long)")):
        out.append('<line x1="%d" x2="%d" y1="%d" y2="%d" stroke="%s" stroke-width="2"%s/>' % (L + 12, L + 36, ly, ly, colour, dash))
        out.append('<text x="%d" y="%d" fill="%s">%s</text>' % (L + 42, ly + 4, INK, html.escape(label)))
        ly += 18
    out.append("</svg>")
    return "\n".join(out) + "\n"


def png(channel, data, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    x, a, b, best, base, adaptive = series(data["points"])
    fig, ax = plt.subplots(figsize=(7.6, 4.4), dpi=150)
    for cfg, pts in sorted(data["fixed"].items()):
        if cfg != base:
            ax.plot([p[0] for p in pts], [p[1] for p in pts], color=OTHER, linewidth=1, zorder=1)
    ax.plot([], [], color=OTHER, linewidth=1, label="other fixed rungs (Long)")
    ax.plot(x, [v if v is not None else float("nan") for v in best], color=AQUA, linewidth=2, linestyle="--", label="best fixed rung at each point", zorder=2)
    for tr, colour, label in ((b, ORANGE, (rung(base) + " fixed, the baseline") if base else "baseline"), (a, BLUE, rung(adaptive))):
        xs = [u for u, t in zip(x, tr) if t[0] is not None]
        ax.fill_between(xs, [t[1] for t in tr if t[0] is not None], [t[2] for t in tr if t[0] is not None], color=colour, alpha=0.15, linewidth=0)
        ax.plot(xs, [t[0] for t in tr if t[0] is not None], color=colour, linewidth=2, marker="o", markersize=5, label=label, zorder=3)
    ax.set_title("%s: goodput against set SNR" % NAMES.get(channel, channel), loc="left", fontsize=11)
    ax.set_xlabel("set SNR (dB in 3 kHz)")
    ax.set_ylabel("goodput (bit/s)")
    ax.set_xticks(x)
    ax.set_ylim(bottom=0)
    ax.grid(axis="y", color=GRID)
    for side in ("top", "right"):
        ax.spines[side].set_visible(False)
    ax.legend(loc="upper left", frameon=False, fontsize=9)
    fig.tight_layout()
    fig.savefig(path)
    plt.close(fig)


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2
    agg = argv[1]
    out = argv[2] if len(argv) > 2 else agg
    os.makedirs(out, exist_ok=True)
    try:
        import matplotlib  # noqa: F401
        have_mpl = True
    except ImportError:
        have_mpl = False
    for channel, data in sorted(load(agg).items()):
        p = os.path.join(out, "curve-%s.svg" % channel)
        with open(p, "w", encoding="ascii") as f:
            f.write(svg(channel, data))
        print(p)
        if have_mpl:
            p = os.path.join(out, "curve-%s.png" % channel)
            png(channel, data, p)
            print(p)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
