#!/usr/bin/env python3
"""把 payload 目录打包成安装器用的 payload.zip（相对路径即安装后的目录结构）。"""
import os
import sys
import zipfile

src = sys.argv[1]
out = sys.argv[2]

if os.path.exists(out):
    os.remove(out)

files = []
for root, dirs, names in os.walk(src):
    for n in names:
        full = os.path.join(root, n)
        rel = os.path.relpath(full, src).replace("\\", "/")
        files.append((full, rel))

total = sum(os.path.getsize(f) for f, _ in files)
done = 0
print(f"{len(files)} files, {total / 1048576:.0f} MB")

with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for full, rel in files:
        z.write(full, rel)
        done += os.path.getsize(full)
        if done % (64 * 1048576) < os.path.getsize(full):
            print(f"  {done / 1048576:.0f}/{total / 1048576:.0f} MB")

print(f"zip = {os.path.getsize(out) / 1048576:.1f} MB -> {out}")
