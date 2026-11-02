"""Copies the cabinets embedded in a Microsoft redistributable exe to <out>/<n>.cab."""
import os
import struct
import sys

data = open(sys.argv[1], "rb").read()
out = sys.argv[2]

pos = count = 0
while (pos := data.find(b"MSCF\0\0\0\0", pos)) >= 0:
    size = struct.unpack("<I", data[pos + 8 : pos + 12])[0]
    if 0 < size <= len(data) - pos:
        with open(os.path.join(out, f"{count}.cab"), "wb") as f:
            f.write(data[pos : pos + size])
        count += 1
        pos += size
    else:
        pos += 4

if count < 2:
    sys.exit(f"Expected at least 2 cabinets, found {count}")
