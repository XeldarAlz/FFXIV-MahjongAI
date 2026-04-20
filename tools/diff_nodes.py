"""Diff two walknodes dumps to find nodes whose visibility flipped.

Usage:
    py -3 tools/diff_nodes.py <prompt.txt> <nopopup.txt>
"""
from __future__ import annotations
import re
import sys
from pathlib import Path

NODE_RE = re.compile(r"type=(\S+)\s+id=(\d+)\s+vis=(\d)")
ROW_RE = re.compile(r"@0x([0-9A-Fa-f]+)")


def nodes(path: Path) -> dict:
    """Parse a walknodes dump. Returns {(id, type): (vis, address, full_line)}."""
    out = {}
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = NODE_RE.search(raw)
        if not m:
            continue
        ntype, nid, vis = m.group(1), int(m.group(2)), int(m.group(3))
        am = ROW_RE.search(raw)
        addr = am.group(1) if am else ""
        key = (nid, ntype)
        if key not in out:
            out[key] = (vis, addr, raw.strip())
    return out


def main() -> None:
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    a = nodes(Path(sys.argv[1]))
    b = nodes(Path(sys.argv[2]))

    print(f"{sys.argv[1]}  -> {len(a)} nodes")
    print(f"{sys.argv[2]}  -> {len(b)} nodes")
    print()

    flipped = []
    for k in sorted(set(a) | set(b)):
        av = a.get(k, (None,))[0]
        bv = b.get(k, (None,))[0]
        if av is not None and bv is not None and av != bv:
            flipped.append((k, av, bv, a[k][2], b[k][2]))

    print("== flipped visibility (A vis -> B vis) ==")
    for (nid, ntype), av, bv, la, lb in flipped:
        print(f"id={nid:<6} type={ntype:<20} A.vis={av} B.vis={bv}")
        print(f"  A: {la[:200]}")
        print(f"  B: {lb[:200]}")

    print()
    print(f"total flipped: {len(flipped)}")

    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    if only_a:
        print(f"\n== only in A (n={len(only_a)}) ==")
        for k in only_a[:40]:
            print(f"  id={k[0]} type={k[1]}  vis={a[k][0]}")
    if only_b:
        print(f"\n== only in B (n={len(only_b)}) ==")
        for k in only_b[:40]:
            print(f"  id={k[0]} type={k[1]}  vis={b[k][0]}")


if __name__ == "__main__":
    main()
