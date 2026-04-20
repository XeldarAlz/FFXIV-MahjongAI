"""Scan a snap file for all [xx, 29, 01, 00] tile-encoded 4-byte patterns.

In the game's memory format, every discarded/melded/held tile is stored as
[tile_id + 9, 0x29, 0x01, 0x00]. Finding regions dense with this pattern pins
the arrays that hold hands, discards, and melds.
"""
import re
import sys
from pathlib import Path

HEX_LINE = re.compile(r"\s*\+0x([0-9A-Fa-f]+):\s+((?:[0-9A-Fa-f]{2}\s+){1,16}[0-9A-Fa-f]{2})")

TILE_NAMES = (
    ["1m","2m","3m","4m","5m","6m","7m","8m","9m"] +
    ["1p","2p","3p","4p","5p","6p","7p","8p","9p"] +
    ["1s","2s","3s","4s","5s","6s","7s","8s","9s"] +
    ["E","S","W","N","Haku","Hatsu","Chun"])

def load_region(text: str, region_header: str, size: int) -> bytes:
    buf = bytearray(size)
    in_region = False
    for line in text.splitlines():
        if region_header in line:
            in_region = True
            continue
        if in_region and "--" in line and region_header not in line:
            break
        if not in_region:
            continue
        m = HEX_LINE.match(line)
        if not m:
            continue
        off = int(m.group(1), 16)
        for i, b in enumerate(m.group(2).split()):
            if off + i < size:
                buf[off + i] = int(b, 16)
    return bytes(buf)

def scan(buf: bytes, region_name: str, base_offset: int = 0):
    """Find every [xx, 29, 01, 00] where tile_id = xx-9 ∈ [0, 33]."""
    hits = []
    for i in range(len(buf) - 3):
        if buf[i+1] == 0x29 and buf[i+2] == 0x01 and buf[i+3] == 0x00:
            tid = buf[i] - 9
            if 0 <= tid < 34:
                hits.append((i, tid))
    # Group into runs (adjacent 4-byte slots).
    runs = []
    i = 0
    while i < len(hits):
        run = [hits[i]]
        j = i + 1
        while j < len(hits) and hits[j][0] == run[-1][0] + 4:
            run.append(hits[j])
            j += 1
        runs.append(run)
        i = j
    print(f"\n## {region_name}  ({len(hits)} hits, {len(runs)} runs)")
    for run in runs:
        start = run[0][0]
        tiles = " ".join(TILE_NAMES[t] for _, t in run)
        end = run[-1][0] + 4
        print(f"  +0x{start:04X}..+0x{end:04X} ({len(run)} tiles): {tiles}")

if __name__ == "__main__":
    path = Path(sys.argv[1])
    text = path.read_text(encoding="utf-8", errors="replace")
    addon = load_region(text, "-- addon @", 0x3000)
    agent = load_region(text, "-- AgentEmj @", 0x2000)
    print(f"# {path.name}")
    scan(addon, "addon")
    scan(agent, "agent")
