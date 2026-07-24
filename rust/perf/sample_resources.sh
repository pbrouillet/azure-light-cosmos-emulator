#!/usr/bin/env bash
# Resource sampler: polls /proc/<pid> for RSS and CPU while a load runs.
#
# Reads VmRSS/VmHWM from /proc/<pid>/status and utime+stime from
# /proc/<pid>/stat, sampling at a fixed interval. Writes a CSV and, on exit,
# prints peak RSS (MiB), the kernel-reported peak (VmHWM), and mean/peak CPU%.
#
# Usage:
#   sample_resources.sh --pid <pid> [--interval 0.2] [--csv out.csv] [--label name]
#
# Run it in the background, then kill it when the load finishes:
#   sample_resources.sh --pid $PID --csv s.csv & SAMP=$!; ...load...; kill $SAMP
#
# CPU% is normalized to a single core (100% = one core fully busy); it can
# exceed 100% on multi-core work.

set -uo pipefail

PID=""
INTERVAL="0.2"
CSV=""
LABEL=""

while [ $# -gt 0 ]; do
  case "$1" in
    --pid) PID="$2"; shift 2 ;;
    --interval) INTERVAL="$2"; shift 2 ;;
    --csv) CSV="$2"; shift 2 ;;
    --label) LABEL="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [ -z "$PID" ]; then echo "--pid is required" >&2; exit 2; fi
if [ ! -d "/proc/$PID" ]; then echo "no such pid: $PID" >&2; exit 1; fi

CLK_TCK="$(getconf CLK_TCK 2>/dev/null || echo 100)"
[ -z "$CSV" ] && CSV="$(mktemp)"
echo "t_s,rss_kib,vmhwm_kib,cpu_pct" > "$CSV"

peak_rss=0
peak_hwm=0
peak_cpu=0
cpu_sum=0
cpu_n=0
t0="$(date +%s.%N)"

prev_cpu_ticks=""
prev_time=""

finish() {
  local mean_cpu peak_rss_mib peak_hwm_mib
  mean_cpu="$(awk -v s="$cpu_sum" -v n="$cpu_n" 'BEGIN{ if(n>0) printf "%.1f", s/n; else print 0 }')"
  peak_rss_mib="$(awk -v k="$peak_rss" 'BEGIN{printf "%.1f", k/1024.0}')"
  peak_hwm_mib="$(awk -v k="$peak_hwm" 'BEGIN{printf "%.1f", k/1024.0}')"
  echo "SAMPLE_RESULT label=${LABEL:-none} peak_rss_mib=$peak_rss_mib peak_vmhwm_mib=$peak_hwm_mib peak_cpu_pct=$peak_cpu mean_cpu_pct=$mean_cpu samples=$cpu_n csv=$CSV"
  exit 0
}
# Print the summary whether we finish naturally or are killed by the orchestrator.
trap finish TERM INT EXIT

read_stat_cpu() { # -> echoes utime+stime ticks, or empty
  local statline
  statline="$(cat "/proc/$PID/stat" 2>/dev/null)" || return 1
  # Fields after the (comm) may contain spaces; strip up to the last ')'.
  local rest="${statline#*) }"
  # shellcheck disable=SC2206
  local f=($rest)
  # After ')', field indexes: state=0 ... utime=11, stime=12 (0-based here).
  local utime="${f[11]}"
  local stime="${f[12]}"
  echo $(( utime + stime ))
}

while [ -d "/proc/$PID" ]; do
  now="$(date +%s.%N)"
  rss="$(awk '/^VmRSS:/{print $2}' "/proc/$PID/status" 2>/dev/null)"
  hwm="$(awk '/^VmHWM:/{print $2}' "/proc/$PID/status" 2>/dev/null)"
  [ -z "$rss" ] && break

  cpu_ticks="$(read_stat_cpu)" || cpu_ticks=""
  cpu_pct=0
  if [ -n "$cpu_ticks" ] && [ -n "$prev_cpu_ticks" ]; then
    dt="$(awk -v a="$now" -v b="$prev_time" 'BEGIN{printf "%.4f", a-b}')"
    dticks=$(( cpu_ticks - prev_cpu_ticks ))
    cpu_pct="$(awk -v d="$dticks" -v tck="$CLK_TCK" -v dt="$dt" 'BEGIN{ if (dt<=0){print 0} else {printf "%.1f", (d/tck)/dt*100.0} }')"
  fi
  prev_cpu_ticks="$cpu_ticks"
  prev_time="$now"

  t_rel="$(awk -v a="$now" -v b="$t0" 'BEGIN{printf "%.2f", a-b}')"
  echo "$t_rel,$rss,${hwm:-0},$cpu_pct" >> "$CSV"

  [ "$rss" -gt "$peak_rss" ] 2>/dev/null && peak_rss="$rss"
  [ -n "$hwm" ] && [ "$hwm" -gt "$peak_hwm" ] 2>/dev/null && peak_hwm="$hwm"
  awk -v c="$cpu_pct" -v p="$peak_cpu" 'BEGIN{exit !(c+0>p+0)}' && peak_cpu="$cpu_pct"
  cpu_sum="$(awk -v s="$cpu_sum" -v c="$cpu_pct" 'BEGIN{printf "%.1f", s+c}')"
  cpu_n=$(( cpu_n + 1 ))

  sleep "$INTERVAL"
done

