#!/usr/bin/env python3
"""Regenerate tests/trace.inc and verify the embedded FM trace prefix.

The probe replays a real, bounded YM2608 FM trace: the first 137 `opna` writes
of XA2047.events.jsonl (a capture of YM2608 bus writes from actual FMP
playback). This script:

  1. reads XA2047.events.jsonl,
  2. takes indices 0..136,
  3. maps port-1 registers to the 0x100 bank bit,
  4. writes tests/trace.inc (the {{addr,val}} table included by the probe),
  5. prints the sha256/FNV-1a checksums the probe's assertions pin to.

Run:  python3 tests/trace.py   (from MDPlayer/native/MDPlayer.OpnaNative)
"""
import hashlib
import json
import os
import sys

def main() -> int:
    # The trace lives at the repository root (tests/trace.py -> MDPlayer.OpnaNative -> native -> MDPlayer -> root).
    here = os.path.dirname(os.path.abspath(__file__))
    root_trace = os.path.abspath(os.path.join(here, "..", "..", "..", ".."))
    trace_path = os.path.join(root_trace, "XA2047.events.jsonl")
    out_path = os.path.join(here, "trace.inc")

    if not os.path.exists(trace_path):
        print("error: cannot find XA2047.events.jsonl at", trace_path, file=sys.stderr)
        return 1

    events = []
    with open(trace_path) as f:
        for ln in f:
            ln = ln.strip()
            if not ln:
                continue
            d = json.loads(ln)
            if d.get("ev") == "opna":
                events.append(d)

    # Bounded prefix: indices 0..136 inclusive.
    rows = []
    for e in events[0:137]:
        addr = (0x100 | e["address"]) if e["port"] == 1 else e["address"]
        rows.append((addr & 0x1ff, e["data"] & 0xff))

    with open(out_path, "w") as f:
        for a, v in rows:
            f.write("    {0x%03x,0x%02x},\n" % (a, v))

    blob = bytes()
    for a, v in rows:
        blob += bytes([(a >> 8) & 0xff, a & 0xff, v])

    h = 2166136261
    for a, v in rows:
        h = ((h ^ (a & 0xff)) * 16777619) & 0xffffffff
        h = ((h ^ ((a >> 8) & 0xff)) * 16777619) & 0xffffffff
        h = ((h ^ v) * 16777619) & 0xffffffff

    print("wrote %d writes to %s" % (len(rows), out_path))
    print("sha256 = %s" % hashlib.sha256(blob).hexdigest())
    print("fnv1a  = %08x" % h)
    print("#define kTraceFnv 0x%08xu" % h)
    return 0

if __name__ == "__main__":
    sys.exit(main())
