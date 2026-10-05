#!/bin/bash
# Build the statistics models used by `cascade`/`repair` from every clean JPEG we have, one model per size kind.
#   tools/make-models.sh [out-dir=models] [exclude-id ...]
# Excluding ids is for tests only (so a test picture does not train its own model).
set -e
out=${1:-models}; shift || true
dll=dotnet/Jpegfix/bin/Release/net10.0/Jpegfix.dll
mkdir -p "$out"
for kind in thumb preview orig; do
  # Real gallery files plus the small synthetic set. The 400 overnight synthetic images are deliberately NOT used: on 21 real mediums they
  # lowered visually close blocks from 85% to 74% (different resampler, so different coefficient statistics).
  files=$(ls testdata/gallery/$kind-*.jpg testdata/synthetic/$kind-*.jpg 2>/dev/null)
  for ex in "$@"; do files=$(echo "$files" | grep -v -- "-$ex.jpg" || true); done
  dotnet $dll train "$out/$kind.json" $files 2>&1 | tail -1
done
ls -la "$out"
