#!/usr/bin/env bash
# Builds the s5066d and s5066-test .debs for one architecture.
#
#   packaging/build-deb.sh <version> [amd64|arm64] [outdir]
#
# Produces <outdir>/s5066d_<version>_<arch>.deb and <outdir>/s5066-test_<version>_<arch>.deb,
# each a self-contained single-file build (no .NET runtime dependency on the target). Not
# trimmed and not NativeAOT: build each architecture on its own native runner. Default outdir
# is <repo>/artifacts.
#
# Layout: PublishSingleFile bundles the managed code and the runtime but leaves native shims
# loose beside the executable, resolved relative to the real path of the binary, so the payload
# goes in /usr/lib/<pkg>/ and /usr/bin/<pkg> is a symlink into it.
#
# ICU: the build is InvariantGlobalization (Directory.Build.props), so there is no libicu
# dependency and the packages install on any libicu version, 80 included.
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> [arch] [outdir]}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64;   TRIPLET=x86_64-linux-gnu ;;
  arm64) RID=linux-arm64; TRIPLET=aarch64-linux-gnu ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found - this needs a Debian-family host" >&2; exit 3; }
command -v readelf >/dev/null || { echo "readelf not found - install binutils" >&2; exit 3; }

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUTDIR="${3:-$ROOT/artifacts}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$OUTDIR"

case "${SOURCE_DATE_EPOCH:-}" in
  ''|*[!0-9]*) CHANGELOG_DATE="$(date -R)" ;;
  *)           CHANGELOG_DATE="$(date -R --date="@$SOURCE_DATE_EPOCH")" ;;
esac

publish() { # <project> <assembly name> <outdir>
  dotnet publish "$ROOT/$1" \
    --configuration Release --runtime "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishAot=false \
    -p:Version="$VERSION" -p:DebugType=none -p:GenerateDocumentationFile=false \
    --output "$3"
}

# Highest symbol version of one family (GLIBC, GLIBCXX) over every regular ELF file staged.
# Detects ELF by magic bytes; symlinks are skipped so a link is not followed to the build
# host's own library.
max_needed() { # <root> <family>
  local root="$1" family="$2" max="" v f
  while IFS= read -r f; do
    [ "$(od -An -tx1 -N4 "$f" 2>/dev/null | tr -d ' \n')" = "7f454c46" ] || continue
    v="$(readelf --version-info "$f" 2>/dev/null | awk '/Version needs section/,0' \
      | grep -oE "${family}_[0-9][0-9.]*" | sed "s/^${family}_//" | sort -uV | tail -1)"
    [ -n "$v" ] && max="$(printf '%s\n%s\n' "$max" "$v" | sort -uV | tail -1)"
  done < <(find "$root" -type f -print)
  printf '%s' "$max"
}

stdcxx_min() { # <GLIBCXX version> -> libstdc++6 major, anchors as in pdn-soundmodem's build
  case "$1" in
    3.4|3.4.[0-9]|3.4.1[0-9]|3.4.2[01]) echo 5 ;;
    3.4.22) echo 6 ;; 3.4.23|3.4.24) echo 7 ;; 3.4.25) echo 8 ;; 3.4.26) echo 9 ;;
    3.4.27|3.4.28) echo 10 ;; 3.4.29) echo 11 ;; 3.4.30) echo 12 ;;
    3.4.31|3.4.32) echo 13 ;; 3.4.33) echo 14 ;; 3.4.34) echo 15 ;;
    *) echo "unknown GLIBCXX_$1 - extend the table in $0" >&2; return 1 ;;
  esac
}

# Common staging for one package: payload, symlink, docs.
stage_pkg() { # <pkg> <publish dir> <executable name>
  local pkg="$1" pub="$2" exe="$3" root="$STAGE/$1/root"
  local lib="/usr/lib/$pkg" doc="/usr/share/doc/$pkg"
  mkdir -p "$root$lib" "$root/usr/bin" "$root$doc" "$root/DEBIAN"
  install -m 0755 "$pub/$exe" "$root$lib/$exe"
  for so in "$pub"/*.so; do
    [ -e "$so" ] || continue
    install -m 0644 "$so" "$root$lib/$(basename "$so")"
  done
  ln -s "../lib/$pkg/$exe" "$root/usr/bin/$exe"
  install -m 0644 "$HERE/copyright" "$root$doc/copyright"
  cat > "$STAGE/$pkg/changelog.Debian" <<CL
$pkg ($VERSION) unstable; urgency=medium

  * Release $VERSION. See https://github.com/M0LTE/M0LTE.S5066/releases

 -- Tom Fanning M0LTE <tom@m0lte.uk>  $CHANGELOG_DATE
CL
  gzip -9n -c "$STAGE/$pkg/changelog.Debian" > "$root$doc/changelog.Debian.gz"
  chmod 0644 "$root$doc/changelog.Debian.gz"
  # SQLite comes from the distribution when the bundle carries one (needs a newer glibc otherwise).
  if [ -e "$root$lib/libe_sqlite3.so" ]; then
    rm -f "$root$lib/libe_sqlite3.so"
    ln -s "../$TRIPLET/libsqlite3.so.0" "$root$lib/libe_sqlite3.so"
    touch "$STAGE/$pkg/needs-sqlite"
  fi
}

# Depends line from the staged ELF files.
depends_for() { # <pkg> <extra>
  local root="$STAGE/$1/root" glibc glibcxx cxx deps
  glibc="$(max_needed "$root" GLIBC)"
  glibcxx="$(max_needed "$root" GLIBCXX)"
  [ -n "$glibc" ] || { echo "could not read a GLIBC floor from $1" >&2; return 4; }
  [ -n "$glibcxx" ] || { echo "could not read a GLIBCXX floor from $1" >&2; return 4; }
  cxx="$(stdcxx_min "$glibcxx")"
  deps="libc6 (>= $glibc), libgcc-s1, libstdc++6 (>= $cxx)"
  [ -e "$STAGE/$1/needs-sqlite" ] && deps="$deps, libsqlite3-0 (>= 3.34)"
  echo "floors for $1 $ARCH: libc6 >= $glibc, libstdc++6 >= $cxx" >&2
  printf '%s%s' "$deps" "${2:+, $2}"
}

build() { # <pkg>
  local pkg="$1" root="$STAGE/$1/root"
  for s in postinst prerm postrm; do [ -f "$root/DEBIAN/$s" ] && { chmod 0755 "$root/DEBIAN/$s"; sh -n "$root/DEBIAN/$s"; }; done
  local deb="$OUTDIR/${pkg}_${VERSION}_${ARCH}.deb"
  dpkg-deb --root-owner-group --build "$root" "$deb" >/dev/null
  echo "built $deb"
}

# ------------------------------------------------------------------ s5066-test
publish tools/s5066-test/S5066Test.csproj s5066-test "$STAGE/pub-test"
stage_pkg s5066-test "$STAGE/pub-test" s5066-test
R="$STAGE/s5066-test/root"
DEPS="$(depends_for s5066-test)"
cat > "$R/DEBIAN/control" <<CTL
Package: s5066-test
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE <tom@m0lte.uk>
Installed-Size: $(du -k -s --exclude=DEBIAN "$R" | cut -f1)
Depends: $DEPS
Section: hamradio
Priority: optional
Homepage: https://github.com/M0LTE/M0LTE.S5066
Description: STANAG 5066 test client
 A command-line client for any STANAG 5066 node that serves the SIS (s5066d,
 or another implementation): send and receive, run scripts, and exercise the
 node's services. Needs no sound card.
 .
 AGPL-3.0-or-later; see /usr/share/doc/s5066-test/copyright.
CTL
build s5066-test

# ---------------------------------------------------------------------- s5066d
publish src/s5066d/s5066d.csproj s5066d "$STAGE/pub-d"
stage_pkg s5066d "$STAGE/pub-d" s5066d
R="$STAGE/s5066d/root"
mkdir -p "$R/usr/lib/systemd/system" "$R/usr/lib/udev/rules.d" "$R/etc/s5066d" "$R/usr/share/doc/s5066d/bench"
install -m 0644 "$HERE/s5066d.service" "$R/usr/lib/systemd/system/s5066d.service"
install -m 0644 "$HERE/70-s5066d.rules" "$R/usr/lib/udev/rules.d/70-s5066d.rules"
# Conffiles: the operator's copy in /etc survives upgrades.
for f in example.json radio-ft450.json radio-ft818.json; do
  install -m 0644 "$ROOT/src/s5066d/$f" "$R/etc/s5066d/$f"
done
printf '%s\n' /etc/s5066d/example.json /etc/s5066d/radio-ft450.json /etc/s5066d/radio-ft818.json \
  > "$R/DEBIAN/conffiles"
# The bench pair is for two nodes on one machine, not for a station: documentation only.
for f in bench-node1.json bench-node2.json; do
  install -m 0644 "$ROOT/src/s5066d/$f" "$R/usr/share/doc/s5066d/bench/$f"
done
install -m 0644 "$ROOT/docs/s5066d-radio.md" "$R/usr/share/doc/s5066d/s5066d-radio.md"
DEPS="$(depends_for s5066d 'libasound2 | libasound2t64, adduser')"
cat > "$R/DEBIAN/control" <<CTL
Package: s5066d
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE <tom@m0lte.uk>
Installed-Size: $(du -k -s --exclude=DEBIAN "$R" | cut -f1)
Depends: $DEPS
Recommends: s5066-test
Section: hamradio
Priority: optional
Homepage: https://github.com/M0LTE/M0LTE.S5066
Description: STANAG 5066 node with a sound card radio medium
 One STANAG 5066 Edition 4 node over the MIL-STD-188-110D modem. Plays and
 records the modem's audio through a USB sound card and keys the transmitter
 through a CM108 interface's GPIO pin; serves the SIS on a TCP port.
 .
 Installs a systemd unit that is NOT enabled: copy one of the files in
 /etc/s5066d to /etc/s5066d/s5066d.json, edit it, then "systemctl enable --now
 s5066d". The service runs as the s5066d user, in the audio group, and the
 package's udev rule gives that group the CM108 hidraw node.
 .
 AGPL-3.0-or-later combined with GPL-3.0 terms (it links pdn-soundmodem);
 see /usr/share/doc/s5066d/copyright.
CTL

cat > "$R/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
case "$1" in
  configure)
    if ! getent passwd s5066d >/dev/null; then
        adduser --system --no-create-home --group s5066d
    fi
    # Sound card access (the unit also sets this; it matters for a manual run as the user).
    if getent group audio >/dev/null; then
        adduser --quiet s5066d audio || true
    fi
    # Apply the hidraw rule to interfaces already plugged in. Skipped without udev (container).
    if [ -d /run/udev ] && command -v udevadm >/dev/null; then
        udevadm control --reload-rules >/dev/null 2>&1 || true
        udevadm trigger --subsystem-match=hidraw --action=change >/dev/null 2>&1 || true
    fi
    echo "s5066d: not enabled. Copy a file from /etc/s5066d to /etc/s5066d/s5066d.json, edit it,"
    echo "        then: systemctl enable --now s5066d"
    ;;
esac
# The unit is deliberately not enabled or started. An upgrade restarts a running service
# onto the new binary.
if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
    systemctl --system daemon-reload >/dev/null || true
    if [ -n "${2:-}" ] && command -v deb-systemd-invoke >/dev/null; then
        deb-systemd-invoke try-restart s5066d.service >/dev/null || true
    fi
fi
exit 0
EOF
cat > "$R/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && [ "$1" = "remove" ] && command -v deb-systemd-invoke >/dev/null; then
    deb-systemd-invoke stop s5066d.service >/dev/null || true
fi
exit 0
EOF
cat > "$R/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
    systemctl --system daemon-reload >/dev/null || true
fi
if [ "$1" = "purge" ]; then
    # s5066d.json is the operator's own file, not shipped by dpkg.
    rm -f /etc/s5066d/s5066d.json
    rmdir --ignore-fail-on-non-empty /etc/s5066d 2>/dev/null || true
    if getent passwd s5066d >/dev/null; then
        deluser --system --quiet s5066d >/dev/null 2>&1 || true
    fi
fi
exit 0
EOF
build s5066d
