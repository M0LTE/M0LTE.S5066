#!/usr/bin/env bash
# End-to-end test of s5066-ip on real tun devices: two network namespaces, each with an s5066d
# (the bench configuration, MS110D over UDP audio across a veth pair) and an s5066-ip, then
# IPv4 and IPv6 pings from one side to the other across the 5066 link.
#
#   sudo tools/s5066-ip/netns-test.sh <s5066d executable> <s5066-ip executable>
#
# Needs root (namespaces, veth, tun), iproute2 and ping. Everything it makes lives in two
# namespaces it deletes on exit, so the host's own networking is untouched. Run it only on a
# throwaway machine or a CI runner.
set -uo pipefail

S5066D="${1:?usage: netns-test.sh <s5066d> <s5066-ip>}"
S5066IP="${2:?usage: netns-test.sh <s5066d> <s5066-ip>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$(dirname "$HERE")")"
[ "$(id -u)" = 0 ] || { echo "needs root" >&2; exit 2; }

NA=s5066ip-a
NB=s5066ip-b
WORK="$(mktemp -d)"
PIDS=()
cleanup() {
  for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done
  sleep 1
  for p in "${PIDS[@]}"; do kill -9 "$p" 2>/dev/null; done
  ip netns del "$NA" 2>/dev/null
  ip netns del "$NB" 2>/dev/null
  echo "== logs =="
  for f in "$WORK"/*.log; do echo "--- $(basename "$f")"; tail -n 40 "$f"; done
  rm -rf "$WORK"
}
trap cleanup EXIT

ip netns add "$NA"
ip netns add "$NB"
ip link add va netns "$NA" type veth peer name vb netns "$NB"
ip -n "$NA" addr add 10.200.0.1/24 dev va
ip -n "$NB" addr add 10.200.0.2/24 dev vb
for n in "$NA" "$NB"; do ip -n "$n" link set lo up; done
ip -n "$NA" link set va up
ip -n "$NB" link set vb up

# The bench pair, with UDP audio across the veth instead of loopback.
sed -e 's/"audio_listen": "127.0.0.1:7300"/"audio_listen": "10.200.0.1:7300"/' \
    -e 's/"audio_peer": "127.0.0.1:7301"/"audio_peer": "10.200.0.2:7301"/' \
    "$ROOT/src/s5066d/bench-node1.json" > "$WORK/node-a.json"
sed -e 's/"audio_listen": "127.0.0.1:7301"/"audio_listen": "10.200.0.2:7301"/' \
    -e 's/"audio_peer": "127.0.0.1:7300"/"audio_peer": "10.200.0.1:7300"/' \
    "$ROOT/src/s5066d/bench-node2.json" > "$WORK/node-b.json"
grep -q 10.200.0.1:7300 "$WORK/node-a.json" && grep -q 10.200.0.1:7300 "$WORK/node-b.json" || { echo "bench configs changed shape" >&2; exit 1; }

cat > "$WORK/ip-a.json" <<'EOF'
{ "sis": "127.0.0.1:5066", "device": "s5066",
  "routes": [ { "prefix": "10.66.2.0/24", "node": "0.0.1.2" }, { "prefix": "fd66:2::/64", "node": "0.0.1.2" } ] }
EOF
cat > "$WORK/ip-b.json" <<'EOF'
{ "sis": "127.0.0.1:5067", "device": "s5066",
  "routes": [ { "prefix": "10.66.1.0/24", "node": "0.0.1.1" }, { "prefix": "fd66:1::/64", "node": "0.0.1.1" } ] }
EOF

ip netns exec "$NA" "$S5066D" "$WORK/node-a.json" > "$WORK/s5066d-a.log" 2>&1 & PIDS+=($!)
ip netns exec "$NB" "$S5066D" "$WORK/node-b.json" > "$WORK/s5066d-b.log" 2>&1 & PIDS+=($!)
sleep 3
ip netns exec "$NA" "$S5066IP" "$WORK/ip-a.json" > "$WORK/s5066-ip-a.log" 2>&1 & PIDS+=($!)
ip netns exec "$NB" "$S5066IP" "$WORK/ip-b.json" > "$WORK/s5066-ip-b.log" 2>&1 & PIDS+=($!)

for side in a b; do
  for _ in $(seq 60); do grep -q "bound to SAP 9" "$WORK/s5066-ip-$side.log" && break; sleep 1; done
  grep -q "bound to SAP 9" "$WORK/s5066-ip-$side.log" || { echo "FAIL s5066-ip $side did not bind"; exit 1; }
done

# The operator's part: addresses and routes on each tun.
ip -n "$NA" addr add 10.66.1.1/32 dev s5066
ip -n "$NA" addr add fd66:1::1/128 dev s5066 nodad
ip -n "$NA" route add 10.66.2.0/24 dev s5066
ip -n "$NA" route add fd66:2::/64 dev s5066
ip -n "$NB" addr add 10.66.2.1/32 dev s5066
ip -n "$NB" addr add fd66:2::1/128 dev s5066 nodad
ip -n "$NB" route add 10.66.1.0/24 dev s5066
ip -n "$NB" route add fd66:1::/64 dev s5066
ip -n "$NA" link show s5066 | tee /dev/stderr | grep -q "mtu 2048" || { echo "FAIL the tun MTU is not the node's 2048"; exit 1; }

FAIL=0
try_ping() { # <description> <ping args...>
  local what="$1"; shift
  for attempt in 1 2 3; do
    if ip netns exec "$NA" ping -n -c 1 -W 120 "$@"; then echo "PASS $what (attempt $attempt)"; return 0; fi
  done
  echo "FAIL $what"; FAIL=1
}
try_ping "IPv4 ping across the 5066 link" -I 10.66.1.1 10.66.2.1
try_ping "IPv6 ping across the 5066 link" -6 -I fd66:1::1 fd66:2::1
# A ping too big for the link with DF set: the kernel or s5066-ip answers with the MTU, nothing is sent.
if ip netns exec "$NA" ping -n -c 1 -W 5 -M do -s 3000 -I 10.66.1.1 10.66.2.1 2>&1 | tee /dev/stderr | grep -qiE "too long|mtu|frag needed"; then
  echo "PASS oversized DF ping refused with the MTU"
else
  echo "FAIL oversized DF ping"; FAIL=1
fi
exit $FAIL
