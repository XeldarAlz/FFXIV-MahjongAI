"""Snap-file analyzer for Doman Mahjong offset discovery.

Parses snap-auto-*.txt files (produced by /mjauto autosnap) and diffs consecutive
pairs to locate the memory fields that changed on each game event.

Usage:
    python analyze_snaps.py <dir> [--events only]          # per-pair summary
    python analyze_snaps.py <dir> --diff A B              # detailed diff of two labels
    python analyze_snaps.py <dir> --findtile T1 T2 ...    # where does tile_id appear

Tile encoding in addon memory: 4 bytes [tile_id+9, 0x29, 0x01, 0x00].
"""
from __future__ import annotations
import argparse
import os
import re
from pathlib import Path

REGION_ADDON = "addon"
REGION_AGENT = "agent"

HEX_LINE = re.compile(r"\s*\+0x([0-9A-Fa-f]+):\s+((?:[0-9A-Fa-f]{2}\s+){1,16}[0-9A-Fa-f]{2})")

def parse_snap(path: Path) -> dict:
    """Return {'meta': {...}, 'addon': bytes(0x3000), 'agent': bytes(0x2000)}."""
    text = path.read_text(encoding="utf-8", errors="replace")
    header = {}
    m = re.search(r"hand=(\S+)", text); header["hand"] = m.group(1) if m else ""
    m = re.search(r"wall=(\d+)", text); header["wall"] = int(m.group(1)) if m else -1
    m = re.search(r"scores=\[([^\]]+)\]", text); header["scores"] = m.group(1) if m else ""
    m = re.search(r"stateCode=(-?\d+)", text); header["state"] = int(m.group(1)) if m else -1
    m = re.search(r"legal=(\S+)", text); header["legal"] = m.group(1).rstrip(",") if m else ""
    m = re.search(r"label='([^']+)'", text); header["label"] = m.group(1) if m else path.stem

    addon = bytearray(0x3000)
    agent = bytearray(0x2000)
    region = None
    for line in text.splitlines():
        if "-- addon @" in line:
            region = REGION_ADDON; continue
        if "-- AgentEmj @" in line:
            region = REGION_AGENT; continue
        if region is None:
            continue
        m = HEX_LINE.match(line)
        if not m:
            continue
        off = int(m.group(1), 16)
        hex_bytes = m.group(2).split()
        buf = addon if region == REGION_ADDON else agent
        for i, b in enumerate(hex_bytes):
            if off + i < len(buf):
                buf[off + i] = int(b, 16)
    return {"meta": header, "addon": bytes(addon), "agent": bytes(agent)}

def diff_bytes(a: bytes, b: bytes, region_name: str) -> list[tuple[int, int, int]]:
    """Returns list of (offset, byte_a, byte_b) where they differ."""
    return [(i, a[i], b[i]) for i in range(min(len(a), len(b))) if a[i] != b[i]]

def summarize_diff(diffs: list[tuple[int, int, int]]) -> str:
    """Group contiguous diff ranges for concise output."""
    if not diffs:
        return "  (no changes)"
    groups = []
    start = prev = diffs[0][0]
    for off, _, _ in diffs[1:]:
        if off == prev + 1:
            prev = off
        else:
            groups.append((start, prev))
            start = prev = off
    groups.append((start, prev))
    lines = []
    for s, e in groups:
        size = e - s + 1
        if size <= 8:
            a_hex = " ".join(f"{x[1]:02X}" for x in diffs if s <= x[0] <= e)
            b_hex = " ".join(f"{x[2]:02X}" for x in diffs if s <= x[0] <= e)
            lines.append(f"  +0x{s:04X}..+0x{e:04X} ({size}B): {a_hex}  =>  {b_hex}")
        else:
            lines.append(f"  +0x{s:04X}..+0x{e:04X} ({size}B)")
    return "\n".join(lines)

def find_tile_positions(buf: bytes, tile_id: int) -> list[int]:
    """Find offsets matching the [tile_id+9, 0x29, 0x01, 0x00] pattern."""
    needle = bytes([tile_id + 9, 0x29, 0x01, 0x00])
    offsets = []
    i = 0
    while i <= len(buf) - 4:
        idx = buf.find(needle, i)
        if idx < 0:
            break
        offsets.append(idx)
        i = idx + 1
    return offsets

def list_snaps(dir: Path) -> list[Path]:
    return sorted(p for p in dir.iterdir() if p.name.startswith("snap-auto-") and p.suffix == ".txt")

def cmd_summary(dir: Path, events_only: bool):
    snaps = list_snaps(dir)
    prev = None
    for p in snaps:
        cur = parse_snap(p)
        m = cur["meta"]
        tag = (f"{m['label']}  st={m['state']:>3}  legal={m['legal']:<10}  "
               f"hand={m['hand']:<20}")
        if prev is not None:
            d_addon = diff_bytes(prev["addon"], cur["addon"], "addon")
            d_agent = diff_bytes(prev["agent"], cur["agent"], "agent")
            if events_only and not d_addon and not d_agent:
                prev = cur
                continue
            print(tag)
            print(f"  d_addon={len(d_addon):>5} bytes   d_agent={len(d_agent):>5} bytes")
            if len(d_addon) > 0 and len(d_addon) < 200:
                print("  addon:")
                print(summarize_diff(d_addon))
            if len(d_agent) > 0 and len(d_agent) < 200:
                print("  agent:")
                print(summarize_diff(d_agent))
        else:
            print(tag + "  (baseline)")
        prev = cur

def cmd_diff(dir: Path, a_label: str, b_label: str):
    snaps = {p.stem.split('-20')[0]: p for p in list_snaps(dir)}
    # Accept partial labels like "auto-005".
    def find(lab):
        cands = [p for k, p in snaps.items() if lab in k]
        if not cands:
            raise SystemExit(f"no snap matches '{lab}'")
        return cands[0]
    a = parse_snap(find(a_label))
    b = parse_snap(find(b_label))
    print(f"# {a['meta']['label']}: hand={a['meta']['hand']} st={a['meta']['state']}")
    print(f"# {b['meta']['label']}: hand={b['meta']['hand']} st={b['meta']['state']}")
    for region in (REGION_ADDON, REGION_AGENT):
        d = diff_bytes(a[region], b[region], region)
        print(f"\n## {region} diff ({len(d)} bytes)")
        print(summarize_diff(d))

def cmd_findtile(dir: Path, tile_ids: list[int]):
    snaps = list_snaps(dir)
    for p in snaps[:20] + snaps[-20:]:
        cur = parse_snap(p)
        print(f"\n# {cur['meta']['label']}  hand={cur['meta']['hand']}  st={cur['meta']['state']}")
        for tid in tile_ids:
            for region in (REGION_ADDON, REGION_AGENT):
                offs = find_tile_positions(cur[region], tid)
                if offs:
                    print(f"  tile {tid} in {region}: {', '.join(f'+0x{o:04X}' for o in offs)}")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir", type=Path)
    ap.add_argument("--events-only", action="store_true")
    ap.add_argument("--diff", nargs=2, metavar=("A", "B"))
    ap.add_argument("--findtile", nargs="+", type=int, metavar="TILE_ID")
    args = ap.parse_args()
    if args.diff:
        cmd_diff(args.dir, args.diff[0], args.diff[1])
    elif args.findtile:
        cmd_findtile(args.dir, args.findtile)
    else:
        cmd_summary(args.dir, args.events_only)

if __name__ == "__main__":
    main()
