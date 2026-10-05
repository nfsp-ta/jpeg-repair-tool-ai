#!/bin/bash
# Visual check of where the repairer is at.  For one image id, corrupt each available size (thumb, preview, orig),
# repair it with a leave-one-out statistics model, and write a picture: one row per size (original size on top, thumbnail at the bottom), columns
# damaged | repaired | original.
#   tools/preview.sh <id> [real|synthetic] [max-seconds=60]      -> testdata/out/<id>-<real|synthetic>.png
# ROWFIX=1 runs the sibling cascade: thumb first, then preview with the repaired thumbnail as reference (in the search and for the
# row-shift / DC post-processing), then orig with the repaired preview. Sequential, so slower than the default parallel run.
# Requires ImageMagick (convert) and the Release build of dotnet/Jpegfix.
set -e
id=$1; src=${2:-real}; T=${3:-60}
[ -n "$id" ] || { sed -n '2,10p' "$0"; exit 1; }
dll=dotnet/Jpegfix/bin/Release/net10.0/Jpegfix.dll
[ "$src" = synthetic ] && dir=testdata/synthetic || dir=testdata/gallery
out=testdata/out; mkdir -p $out; w=$(mktemp -d)
repair() {   # kind [sibling repaired jpeg]
  local kind=$1 sib=$2 f=$dir/$1-$id.jpg extra=""
  [ -f "$f" ] || return 0
  [ -n "$sib" ] && [ -f "$sib" ] && extra="--rowfix --sibling $sib"
  dotnet $dll train $w/$kind.model $(ls testdata/gallery/$kind-*.jpg testdata/synthetic/$kind-*.jpg 2>/dev/null | grep -v -- "-$id.jpg") 2>/dev/null
  dotnet $dll corrupt "$f" $w/$kind.bad.jpg
  ROWFIX=0 dotnet $dll repair $w/$kind.bad.jpg $w/$kind.fix.jpg --model $w/$kind.model --max-seconds $T $extra 2>&1 | grep -E "STUCK|OK|WARNING|inserted|sibling" | tr '\n' ' ' | sed "s/^/$kind: /" >> $w/log || true
  echo >> $w/log
}
if [ "${ROWFIX:-0}" = 1 ]; then
  repair thumb; repair preview $w/thumb.fix.jpg; repair orig $w/preview.fix.jpg
else
  for k in thumb preview orig; do repair $k & done; wait
fi
row() {   # kind
  local kind=$1 f=$dir/$1-$id.jpg
  [ -f "$f" ] || return 0
  local filter=Lanczos; [ $kind = thumb ] && filter=Point
  for v in bad fix; do convert $w/$kind.$v.jpg -filter $filter -resize 480x480 $w/$kind.$v.png 2>/dev/null || true; done
  convert "$f" -filter $filter -resize 480x480 $w/$kind.good.png
  convert $w/$kind.bad.png $w/$kind.fix.png $w/$kind.good.png -background gray20 -gravity center -extent 480x480 +append $w/row-$kind.png 2>/dev/null
}
for k in thumb preview orig; do row $k; done
rows=(); for k in orig preview thumb; do [ -f $w/row-$k.png ] && rows+=($w/row-$k.png); done   # large to small
convert "${rows[@]}" -background gray20 -append "$out/$id-$src.png"
sort $w/log; echo "wrote $out/$id-$src.png  (columns: damaged | repaired | original)"
rm -rf $w
