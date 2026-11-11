#!/usr/bin/env bash
# Install test for the s5066d, s5066-test, s5066-ip and s5066-mail .debs in a throwaway Debian 13 container.
#
#   packaging/build-deb.sh 0.0.0-test amd64
#   packaging/test-deb.sh [version] [debdir] [arch]
#
# arch defaults to amd64 and must be the host's own (the container runs natively). systemd is
# not running in the container; this checks files, the user, the unit file, that the
# binaries start and load ALSA, an upgrade that keeps edited conffiles, and remove and purge.
# Needs Docker. Nothing is installed on the host.
set -uo pipefail

VERSION="${1:-0.0.0-test}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DEBDIR="$(cd "${2:-$(dirname "$HERE")/artifacts}" && pwd)"
ARCH="${3:-amd64}"
for p in s5066d s5066-test s5066-ip s5066-mail; do
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
if ! apt-get install -y -qq "/debs/s5066d_${VERSION}_${ARCH}.deb" "/debs/s5066-test_${VERSION}_${ARCH}.deb" "/debs/s5066-ip_${VERSION}_${ARCH}.deb" "/debs/s5066-mail_${VERSION}_${ARCH}.deb" >/tmp/apt.log 2>&1; then
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
# No sound card here, so a radio config must fail cleanly at the capture device: that proves
# libasound loads and the error is the friendly one, not an unhandled exception.
OUT="$(timeout 20 s5066d /etc/s5066d/radio-ft818.json 2>&1 </dev/null)"; RC=$?
echo "$OUT" | head -3 | sed 's/^/    /'
check "radio config reaches ALSA"   "[ $RC -eq 1 ] && echo \"\$OUT\" | grep -q capture_device && ! echo \"\$OUT\" | grep -qi 'libasound\|unhandled'"

echo "== s5066-ip =="
check "s5066-ip installed"          "dpkg -l s5066-ip | grep -q '^ii'"
check "s5066-ip on PATH"            "command -v s5066-ip >/dev/null && [ -x /usr/lib/s5066-ip/s5066-ip ]"
check "s5066-ip unit installed"     "grep -q '^ExecStart=/usr/bin/s5066-ip /etc/s5066-ip/s5066-ip.json' /usr/lib/systemd/system/s5066-ip.service"
check "s5066-ip unit has the cap"   "grep -q '^AmbientCapabilities=CAP_NET_ADMIN' /usr/lib/systemd/system/s5066-ip.service && grep -q '^Type=notify' /usr/lib/systemd/system/s5066-ip.service"
check "s5066-ip conffile"           "[ -f /etc/s5066-ip/example.json ] && dpkg-query -W -f='\${Conffiles}' s5066-ip | grep -q /etc/s5066-ip/example.json"
check "s5066-ip unit not enabled"   "[ ! -e /etc/systemd/system/multi-user.target.wants/s5066-ip.service ]"
check "s5066-ip doc and copyright"  "[ -f /usr/share/doc/s5066-ip/copyright ] && [ -f /usr/share/doc/s5066-ip/s5066-ip.md ]"
OUT="$(s5066-ip --help 2>&1)"; RC=$?; echo "$OUT" | head -2 | sed 's/^/    /'
check "s5066-ip --help prints usage" "[ $RC -eq 2 ] && echo \"\$OUT\" | grep -qi usage"
# The example parses; with no /dev/net/tun (or no CAP_NET_ADMIN) in the container it stops at the device, cleanly.
OUT="$(timeout 20 s5066-ip /etc/s5066-ip/example.json 2>&1 </dev/null)"; RC=$?; echo "$OUT" | head -2 | sed 's/^/    /'
check "s5066-ip example reaches the tun" "[ $RC -eq 1 ] && echo \"\$OUT\" | grep -q 'tun' && ! echo \"\$OUT\" | grep -qi 'unhandled'"
apt-get purge -y -qq s5066-ip >/dev/null 2>&1
check "s5066-ip purged clean"       "[ ! -e /etc/s5066-ip ] && [ ! -e /usr/bin/s5066-ip ]"

echo "== s5066-mail =="
check "s5066-mail installed"        "dpkg -l s5066-mail | grep -q '^ii'"
check "s5066-mail on PATH"          "command -v s5066-mail >/dev/null && [ -x /usr/lib/s5066-mail/s5066-mail ]"
check "s5066-mail unit installed"   "grep -q '^ExecStart=/usr/bin/s5066-mail /etc/s5066-mail/s5066-mail.json' /usr/lib/systemd/system/s5066-mail.service && grep -q '^User=s5066-mail' /usr/lib/systemd/system/s5066-mail.service"
check "s5066-mail conffile"         "[ -f /etc/s5066-mail/example.json ] && dpkg-query -W -f='\${Conffiles}' s5066-mail | grep -q /etc/s5066-mail/example.json"
check "s5066-mail user exists"      "getent passwd s5066-mail >/dev/null"
check "s5066-mail unit not enabled" "[ ! -e /etc/systemd/system/multi-user.target.wants/s5066-mail.service ]"
check "s5066-mail doc and copyright" "[ -f /usr/share/doc/s5066-mail/copyright ] && [ -f /usr/share/doc/s5066-mail/s5066-mail.md ]"
OUT="$(s5066-mail --help 2>&1)"; RC=$?; echo "$OUT" | head -2 | sed 's/^/    /'
check "s5066-mail --help prints usage" "[ $RC -eq 2 ] && echo \"\$OUT\" | grep -qi usage"
# The example parses and starts as the service user with its spool; no node is running here, so it
# says so and keeps trying.
mkdir -p /var/lib/s5066-mail && chown s5066-mail:s5066-mail /var/lib/s5066-mail
OUT="$(timeout 5 setpriv --reuid=s5066-mail --regid=s5066-mail --init-groups s5066-mail /etc/s5066-mail/example.json 2>&1 </dev/null)"
echo "$OUT" | head -3 | sed 's/^/    /'
check "s5066-mail example starts"   "echo \"\$OUT\" | grep -q 's5066-mail: node 127.0.0.1:5066, relay 127.0.0.1:25' && echo \"\$OUT\" | grep -q 'trying again' && ! echo \"\$OUT\" | grep -qi 'unhandled'"
apt-get purge -y -qq s5066-mail >/dev/null 2>&1
check "s5066-mail purged, user gone" "! getent passwd s5066-mail >/dev/null && [ ! -e /etc/s5066-mail ] && [ ! -e /usr/bin/s5066-mail ]"

echo "== upgrade =="
# The same package as a newer version: edited conffiles and the operator's s5066d.json survive.
echo '// edited' >> /etc/s5066d/example.json
cp /etc/s5066d/radio-ft818.json /etc/s5066d/s5066d.json
mkdir -p /tmp/up
dpkg-deb -R "/debs/s5066d_${VERSION}_${ARCH}.deb" /tmp/up/pkg
sed -i "s/^Version: .*/Version: ${VERSION}+up1/" /tmp/up/pkg/DEBIAN/control
dpkg-deb --root-owner-group -b /tmp/up/pkg /tmp/up/s5066d-up.deb >/dev/null
if ! dpkg -i /tmp/up/s5066d-up.deb </dev/null >/tmp/up.log 2>&1; then cat /tmp/up.log; bad "upgrade"; fi
check "upgraded to the new version" "[ \"\$(dpkg-query -W -f='\${Version}' s5066d)\" = '${VERSION}+up1' ]"
check "edited conffile kept"        "tail -1 /etc/s5066d/example.json | grep -q edited"
check "s5066d.json kept"            "[ -f /etc/s5066d/s5066d.json ]"
check "user still there"            "getent passwd s5066d >/dev/null"

echo "== remove and purge =="
# As if the operator had enabled the unit (systemd is not running here).
mkdir -p /etc/systemd/system/multi-user.target.wants
ln -sf /usr/lib/systemd/system/s5066d.service /etc/systemd/system/multi-user.target.wants/s5066d.service
apt-get remove -y -qq s5066d >/dev/null 2>&1
check "removed, conffile kept"      "[ -f /etc/s5066d/example.json ] && [ ! -e /usr/bin/s5066d ]"
apt-get purge -y -qq s5066d >/dev/null 2>&1
check "purged, user gone"           "! getent passwd s5066d >/dev/null"
check "purged, conffiles gone"      "[ ! -e /etc/s5066d/example.json ] && [ ! -e /etc/s5066d/radio-ft450.json ]"
check "purged, s5066d.json kept"    "[ -f /etc/s5066d/s5066d.json ]"
check "purged, enable link gone"    "[ ! -L /etc/systemd/system/multi-user.target.wants/s5066d.service ]"
rm -f /etc/s5066d/s5066d.json
apt-get install -y -qq "/debs/s5066d_${VERSION}_${ARCH}.deb" >/dev/null 2>&1
apt-get purge -y -qq s5066d >/dev/null 2>&1
check "purged clean, /etc/s5066d gone" "[ ! -e /etc/s5066d ]"
exit $FAIL
INNER
chmod +x "$WORK/inner.sh"
docker run --rm -v "$DEBDIR:/debs:ro" -v "$WORK:/work:ro" debian:trixie bash /work/inner.sh
