#!/bin/bash
# Unattended local benchmark run.  Downloads extra clean car photos (Wikimedia Commons), builds synthetic triplets with the
# forum's encoder settings, then sweeps the search constants one at a time on those triplets and finally checks the best
# settings against the real gallery files.  Runs entirely locally after the download step; resumable; failures in one step
# do not stop the rest.  Everything is written under testdata/overnight/ (git-ignored).
#
#   nohup tools/overnight.sh > testdata/overnight/run.log 2>&1 &         # full run, ~3-4 h
#   SMOKE=1 tools/overnight.sh                                           # ~5 min end-to-end test with tiny samples
#   systemd-inhibit --what=sleep tools/overnight.sh ...                  # keep the machine awake while it runs
#
# Results: testdata/overnight/results.txt (one line per run) and testdata/overnight/summary.md (ranked, written at the end
# and after every sweep step, so a partial run still has a summary).
cd "$(dirname "$0")/.." || exit 1
O=testdata/overnight; mkdir -p $O
D="dotnet dotnet/Jpegfix/bin/Release/net10.0/Jpegfix.dll"
THREADS=${THREADS:-$(( $(nproc) - 2 ))}; [ "$THREADS" -ge 1 ] || THREADS=1
if [ -n "$SMOKE" ]; then N=6; LIM_T=12; LIM_P=4; LIM_O=3; SEC_P=30; SEC_O=30; SEC_T=15; else N=${N:-400}; LIM_T=${LIM_T:-150}; LIM_P=${LIM_P:-40}; LIM_O=${LIM_O:-25}; SEC_P=${SEC_P:-90}; SEC_O=${SEC_O:-150}; SEC_T=${SEC_T:-30}; fi
RES=$O/results.txt; [ -n "$SMOKE" ] && RES=$O/results-smoke.txt && SUM=$O/summary-smoke.md || SUM=$O/summary.md
: > $RES
echo "== $(date) start (threads=$THREADS N=$N)"

echo "== build"; (cd dotnet/Jpegfix && dotnet build -c Release 2>&1 | tail -2) || exit 1

echo "== fetch"; node tools/fetch-commons.js $O/src $N 2>&1 | tail -3
echo "== triplets"; tools/make-synthetic-triplets.sh $O/src $O/synthetic 2>&1 | tail -1

run() {   # label kind limit seconds dirs [env...]
  local label=$1 kind=$2 lim=$3 sec=$4 dirs=$5; shift 5
  local line; line=$(env "$@" nice timeout 3h $D bench $dirs --kind $kind --limit $lim --max-seconds $sec --threads $THREADS 2>/dev/null | tail -1)
  echo "$label | $kind | $line" >> $RES
  echo "$(date +%H:%M) $label $kind: $line" | sed 's/mean blocks reached=[0-9]*%  //'
}
sweep() {  # label env...
  local label=$1; shift
  run "$label" thumb   $LIM_T $SEC_T $O/synthetic "$@"
  run "$label" preview $LIM_P $SEC_P $O/synthetic "$@"
  run "$label" orig    $LIM_O $SEC_O $O/synthetic "$@"
  node tools/summarize-overnight.js $RES > $SUM 2>/dev/null
}

echo "== sweeps (defaults: W_M=1 LOOK=2 INS_PEN=40 LOOKW=0.25 BEAMDELTA=80)"
sweep "default" X=1
if [ -z "$SMOKE" ]; then
  for v in 20 30 60; do sweep "INS_PEN=$v" INS_PEN=$v; done
  for v in 0.1 0.5;  do sweep "LOOKW=$v" LOOKW=$v; done
  for v in 40 120;   do sweep "BEAMDELTA=$v" BEAMDELTA=$v; done
  for v in 1 3;      do sweep "LOOK=$v" LOOK=$v; done
  for v in 0.75 1.5; do sweep "W_M=$v" W_M=$v; done
  for v in 15 40;    do sweep "FAILPEN=$v" FAILPEN=$v; done
  sweep "CHROMA=dc" CHROMA=dc
else
  sweep "INS_PEN=60" INS_PEN=60
fi

echo "== validation on the real gallery files (model also trained on the synthetic set)"
for cfg in "default|X=1" "$(node tools/summarize-overnight.js $RES --best-env 2>/dev/null)"; do
  label=${cfg%%|*}; envs=${cfg#*|}; [ -n "$label" ] || continue
  for k in thumb:120:$SEC_T preview:23:$SEC_P orig:14:$SEC_O; do IFS=: read kind lim sec <<< "$k"
    [ -n "$SMOKE" ] && lim=3
    run "REAL $label" $kind $lim $sec testdata/gallery,$O/synthetic EVAL_FIRST_DIR=1 $envs
  done
done
node tools/summarize-overnight.js $RES > $SUM 2>/dev/null
echo "== $(date) done; see $SUM"
