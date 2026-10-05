#!/bin/bash
# One-time, read-only copy of the damaged gallery from the network share to local disk (reading each file over the share
# is slow; local reads make every later survey/repair run fast). Resumable: files already copied are skipped. The source is never modified.
#   tools/copy-gallery.sh <source dir (the share's gvfs path)> <destination dir> [parallel jobs=6]
set -e
src=${1%/}; dest=${2%/}; jobs=${3:-6}
[ -d "$src" ] && [ -n "$dest" ] || { sed -n '2,6p' "$0"; exit 1; }
mkdir -p "$dest"
copy_one() {
  local f=$1 rel d
  rel=${f#"$src"/}; d="$dest/$(dirname "$rel")"
  mkdir -p "$d"
  [ -s "$dest/$rel" ] && return 0
  local tmp="$d/.part.$$.$(basename "$rel")"
  if gio copy "$f" "$tmp" 2>/dev/null; then mv "$tmp" "$dest/$rel"; else rm -f "$tmp"; echo "FAILED: $rel" >&2; fi
}
export -f copy_one; export src dest
find "$src" -type f -print0 | xargs -0 -P "$jobs" -I{} bash -c 'copy_one "$1"' _ {}
echo "copied $(find "$dest" -type f | wc -l) files, $(du -sh "$dest" | cut -f1)"
