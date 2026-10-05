#!/bin/bash
# Stage-1 repair of one real gallery file into testdata/out/stage1/<kind>-<id>.jpg (leave-one-out model, like preview.sh).
#   tools/stage1.sh <kind> <id> [max-seconds=150]
set -e
kind=$1; id=$2; T=${3:-150}
dll=dotnet/Jpegfix/bin/Release/net10.0/Jpegfix.dll
out=testdata/out/stage1; mkdir -p $out; w=$(mktemp -d)
dotnet $dll train $w/m $(ls testdata/gallery/$kind-*.jpg testdata/synthetic/$kind-*.jpg 2>/dev/null | grep -v -- "-$id.jpg") 2>/dev/null
dotnet $dll corrupt testdata/gallery/$kind-$id.jpg $w/bad.jpg
dotnet $dll repair $w/bad.jpg $out/$kind-$id.jpg --model $w/m --max-seconds $T >/dev/null 2>&1 || true
rm -rf $w
