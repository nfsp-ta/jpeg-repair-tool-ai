#!/bin/bash
# Build synthetic orig/preview/thumb triplets from the clean originals in testdata/gallery, using ImageMagick
# (libjpeg) with the forum's settings: q95, 4:2:0, standard (non-optimised) Huffman tables, no restart markers.
# Verified: DQT, SOF and DHT segments are byte-identical to the real gallery files; the resampling filter is only an
# approximation (real thumbs match Lanczos sizes within ~1%, real mediums land between Mitchell and Lanczos).
#   tools/make-synthetic-triplets.sh [srcDir=testdata/gallery] [outDir=testdata/synthetic]
# Output names follow the gallery scheme (orig-<id>.jpg, preview-<id>.jpg, thumb-<id>.jpg) so `bench` can read the dir.
set -e
src=${1:-testdata/gallery}; out=${2:-testdata/synthetic}; mkdir -p "$out"
opts=(-quality 95 -sampling-factor 2x2,1x1,1x1 -define jpeg:optimize-coding=false -strip)
for f in "$src"/orig-*.jpg; do
  id=$(basename "$f" .jpg); id=${id#orig-}
  convert "$f" "${opts[@]}" "$out/orig-$id.jpg"
  convert "$f" -filter Lanczos -resize 640x640 "${opts[@]}" "$out/preview-$id.jpg"
  convert "$f" -filter Lanczos -resize 120x120 "${opts[@]}" "$out/thumb-$id.jpg"
done
echo "wrote $(ls "$out" | wc -l) files to $out"
