#!/usr/bin/env bash
# Install test for the s5066d and s5066-test .debs in a throwaway Debian 13 container.
#
#   packaging/build-deb.sh 0.0.0-test amd64
#   packaging/test-deb.sh [version] [debdir] [arch]
#
# arch defaults to amd64 and must be the host's own (the container runs natively). systemd is
# not running in the container; this checks files, the user, the unit file and that the
# binaries start. Needs Docker. Nothing is installed on the host.
set -uo pipefail

VERSION="${1:-0.0.0-test}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DEBDIR="$(cd "${2:-$(dirname "$HERE")/artifacts}" && pwd)"
ARCH="${3:-amd64}"
for p in s5066d s5066-test; do
  [ -f "$DEBDIR/${p}_${VERSION}_${ARCH}.deb" ] || { echo "no $DEBDIR/${p}_${VERSION}_${ARCH}.deb" >&2; exit 2; }
done
command -v docker >/dev/null || { echo "docker is required" >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/inner.sh" <<INNER
#!/bin/bash
VERSION="$VERSION"
ARCH="$ARCH"
INNER
cat >> "$WORK/inner.sh" <<'INNER'
set -uo pipefail
FAIL=0
ok(){ echo "  PASS  $1"; }
bad(){ echo "  FAIL  $1"; FAIL=1; }
check(){ if eval "$2"; then ok "$1"; else bad "$1"; fi; }

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null 2>&1
if ! apt-get install -y -qq "/debs/s5066d_${VERSION}_${ARCH}.deb" "/debs/s5066-test_${VERSION}_${ARCH}.deb" >/tmp/apt.log 2>&1; then
  cat /tmp/apt.log; bad "apt install"
fi

echo "== install =="
check "s5066d installed"            "dpkg -l s5066d | grep -q '^ii'"
check "s5066-test installed"        "dpkg -l s5066-test | grep -q '^ii'"
check "s5066d on PATH"              "command -v s5066d >/dev/null"
check "s5066-test on PATH"          "command -v s5066-test >/dev/null"
check "payload in /usr/lib"         "[ -x /usr/lib/s5066d/s5066d ] && [ -x /usr/lib/s5066-test/s5066-test ]"
check "unit file installed"         "[ -f /usr/lib/systemd/system/s5066d.service ]"
check "unit has ExecStart"          "grep -q '^ExecStart=/usr/bin/s5066d /etc/s5066d/s5066d.json' /usr/lib/systemd/system/s5066d.service"
check "udev rule installed"         "grep -q 0d8c /usr/lib/udev/rules.d/70-s5066d.rules"
for f in example radio-ft450 radio-ft818; do
  check "conffile $f.json"          "[ -f /etc/s5066d/$f.json ] && dpkg-query -W -f='\${Conffiles}' s5066d | grep -q /etc/s5066d/$f.json"
done
check "user s5066d exists"          "getent passwd s5066d >/dev/null"
check "user is in group audio"      "id -nG s5066d | tr ' ' '\n' | grep -qx audio"
check "unit is not enabled"         "[ ! -e /etc/systemd/system/multi-user.target.wants/s5066d.service ]"
check "no s5066d.json seeded"       "[ ! -e /etc/s5066d/s5066d.json ]"
check "doc and copyright"           "[ -f /usr/share/doc/s5066d/copyright ] && [ -f /usr/share/doc/s5066d/s5066d-radio.md ]"
echo "== run =="
# s5066d exits 2 for --help and says usage on stderr; the check is that the runtime starts.
OUT="$(s5066d --help 2>&1)"; echo "$OUT" | sed 's/^/    /'
check "s5066d --help prints usage"  "echo \"\$OUT\" | grep -qi usage"
OUT="$(s5066-test --help 2>&1 </dev/null)"; echo "$OUT" | head -5 | sed 's/^/    /'
check "s5066-test starts"           "echo \"\$OUT\" | grep -qi 'usage\|s5066-test'"
OUT="$(s5066d /etc/s5066d/example.json 2>&1 </dev/null & PID=$!; sleep 3; kill $PID 2>/dev/null; wait $PID 2>/dev/null)"
echo "$OUT" | head -5 | sed 's/^/    /'
check "s5066d example.json starts"  "echo \"\$OUT\" | grep -q 's5066d: node'"

echo "== remove and purge =="
apt-get remove -y -qq s5066d >/dev/null 2>&1
check "removed, conffile kept"      "[ -f /etc/s5066d/example.json ] && [ ! -e /usr/bin/s5066d ]"
apt-get purge -y -qq s5066d >/dev/null 2>&1
check "purged, user gone"           "! getent passwd s5066d >/dev/null"
check "purged, /etc/s5066d gone"    "[ ! -e /etc/s5066d ]"
exit $FAIL
INNER
chmod +x "$WORK/inner.sh"
docker run --rm -v "$DEBDIR:/debs:ro" -v "$WORK:/work:ro" debian:trixie bash /work/inner.sh
