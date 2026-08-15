#!/bin/bash
#
# Installs the .NET SDK this repository builds with, plus PowerShell, into a Claude Code on the
# web container. Without it a web session cannot compile or test anything, and the agent working
# in it can only reason about whether the code is correct.
#
# The trap this exists to record:
#
#   The official installer is `https://dot.net/v1/dotnet-install.sh`, which redirects to
#   `builds.dotnet.microsoft.com`. Both are refused by the agent proxy with a 403 at CONNECT, as
#   are `download.visualstudio.microsoft.com`, `dotnetbuilds.azureedge.net` and `ci.dot.net`.
#   `packages.microsoft.com` is allowed, and it serves the same binaries as Debian packages. So
#   the SDK is unpacked out of .deb files rather than installed by the vendor's script. Nothing
#   is registered with dpkg and no root-owned paths are touched: the archives are extracted into
#   $HOME and the environment points at them.
#
#   The Ubuntu feed is not an option even though this container is Ubuntu 24.04 — Microsoft's
#   `ubuntu/24.04/prod` feed carries no .NET 10 at all (its newest dotnet package is 6.0). The
#   Debian 12 build runs correctly here; a .NET built against glibc 2.36 is fine on 2.39.
#
# Versions are pinned and every download is checked against the SHA-256 the package index
# publishes, for the same reason `docs/NATIVE-BINARIES.md` pins the parakeet.cpp release: a
# toolchain that follows a moving tag makes a build failure a question about what changed
# upstream this week. Digests below were read from the feed's own Packages index and confirmed
# against the downloaded files.
#
# SDK 10.0.400 is what `global.json` resolves to (it asks for 10.0.100 with
# `rollForward: latestFeature`) and is the version the project is developed against.
# PowerShell 7.6.4 matches the maintainer's machine; the scripts in `scripts/` cannot *run* here
# because they need Windows and the vendored natives, but they can be parsed, and a syntax error
# shipped to somebody else's machine is a wasted round trip.
#
# Re-running is free: an SDK already in place is detected and the downloads are skipped.
#
# DUPLICATION, DELIBERATE: scripts/cloud-setup.sh installs the same toolchain with the same digests
# into the cloud environment's own setup-script field. That one runs at container creation on any
# branch; this one runs per session and only once it is on the default branch. They cannot share
# code because that file has to be self-contained to be pasted into a text box, so if the pinned
# versions change, both change. They do not fight: whichever runs first is adopted by the other,
# detected by SDK version rather than by path.

set -euo pipefail

# Local machines have their own toolchains and their own opinions about where they live.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    exit 0
fi

DOTNET_HOME="$HOME/.dotnet"
PWSH_HOME="$HOME/.local/opt/powershell"
FEED="https://packages.microsoft.com/debian/12/prod"

log() { printf '[session-start] %s\n' "$*"; }
fail() { printf '[session-start] ERROR: %s\n' "$*" >&2; exit 1; }

# Another mechanism may have installed the toolchain already: scripts/cloud-setup.sh puts the same
# pinned build in /opt at container creation, on any branch, before this ever runs. Downloading a
# second byte-identical copy would cost about 215 MB of transfer and a gigabyte of a per-session
# disk allowance that is fixed, so an SDK that is already reachable is adopted rather than
# duplicated. Checked by version, not by path, so it does not matter which mechanism won.
existing_sdk() {
    local candidate
    for candidate in "$DOTNET_HOME" /opt/dotnet /usr/share/dotnet "$(command -v dotnet >/dev/null 2>&1 && dirname "$(readlink -f "$(command -v dotnet)")")"; do
        [ -n "$candidate" ] && [ -x "$candidate/dotnet" ] || continue
        "$candidate/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.0\.400 ' || continue
        printf '%s' "$candidate"
        return 0
    done
    return 1
}

existing_pwsh() {
    local candidate
    for candidate in "$PWSH_HOME" /opt/powershell "$(command -v pwsh >/dev/null 2>&1 && dirname "$(readlink -f "$(command -v pwsh)")")"; do
        [ -n "$candidate" ] && [ -x "$candidate/pwsh" ] || continue
        printf '%s' "$candidate"
        return 0
    done
    return 1
}

# name  sha256  path-under-$FEED
read -r -d '' PACKAGES <<'EOF' || true
dotnet-host ba4047cfe4ac6bb6c8cc7bd66725ba3c5d7dca237d61dc2fa651322f1c82d642 pool/main/d/dotnet-host/dotnet-host_10.0.11-1_amd64.deb
dotnet-hostfxr 3d019e677ea2c976246df262a064def729d64e9338b1abd3ba2e7cec36937a3b pool/main/d/dotnet-hostfxr-10.0/dotnet-hostfxr-10.0_10.0.11-1_amd64.deb
dotnet-runtime 2fe21a581d608e1370367b0daf52136dc379febe4662cbf4e1599edb85822580 pool/main/d/dotnet-runtime-10.0/dotnet-runtime-10.0_10.0.11-1_amd64.deb
dotnet-targeting-pack 19ed2ea510143eac785b513f6e987f909c5a436c527b645417df8807985a1488 pool/main/d/dotnet-targeting-pack-10.0/dotnet-targeting-pack-10.0_10.0.11-1_amd64.deb
dotnet-apphost-pack b00712332658ba461cb1eb7a187251734486d1cff9d54a8c41d44b7c6cee8269 pool/main/d/dotnet-apphost-pack-10.0/dotnet-apphost-pack-10.0_10.0.11-1_amd64.deb
netstandard-targeting-pack 0f12001d1918f7ad2452d14d70bd396c82080b691407735213e90de637061f57 pool/main/n/netstandard-targeting-pack-2.1/netstandard-targeting-pack-2.1_2.1.0-1_amd64.deb
dotnet-sdk e42c102495d7f4813880a7c29fa02f1f58544e3a08099968c48858e218b1b6c8 pool/main/d/dotnet-sdk-10.0/dotnet-sdk-10.0_10.0.400-1_amd64.deb
powershell e5688e0569568d48051c49d3e93504cde47af709cdaaabd9a8892bc676b3bdf3 pool/main/p/powershell/powershell_7.6.4-1.deb_amd64.deb
EOF

# Unpacks one .deb into $2 after checking its digest. dpkg-deb is present on this image; the
# ar/tar path is the fallback for one that has only binutils.
fetch_and_unpack() {
    local name="$1" digest="$2" path="$3" into="$4"
    local file="$CACHE/$(basename "$path")"

    if [ ! -f "$file" ] || [ "$(sha256sum "$file" | cut -d' ' -f1)" != "$digest" ]; then
        log "downloading $name"
        curl -fsSL --retry 3 --retry-delay 2 --max-time 600 -o "$file" "$FEED/$path" \
            || fail "could not download $name from $FEED/$path"
    fi

    local actual
    actual="$(sha256sum "$file" | cut -d' ' -f1)"
    if [ "$actual" != "$digest" ]; then
        # Never unpack it anyway. A toolchain that is not the one that was pinned is the kind of
        # difference that shows up later as a build that behaves differently for no visible reason.
        fail "$name digest mismatch: expected $digest, got $actual"
    fi

    if command -v dpkg-deb >/dev/null 2>&1; then
        dpkg-deb -x "$file" "$into"
    else
        ( cd "$CACHE" && ar x "$file" && tar xf data.tar.* -C "$into" && rm -f data.tar.* control.tar.* debian-binary )
    fi
}

if found="$(existing_sdk)"; then
    DOTNET_HOME="$found"
    if found_pwsh="$(existing_pwsh)"; then PWSH_HOME="$found_pwsh"; fi
    log "SDK 10.0.400 already present at $DOTNET_HOME — nothing to download"
else
    CACHE="$HOME/.cache/uindosill-toolchain"
    STAGE="$(mktemp -d)"
    mkdir -p "$CACHE"
    trap 'rm -rf "$STAGE"' EXIT

    while read -r name digest path; do
        [ -n "${name:-}" ] || continue
        fetch_and_unpack "$name" "$digest" "$path" "$STAGE"
    done <<< "$PACKAGES"

    mkdir -p "$DOTNET_HOME"
    cp -a "$STAGE/usr/share/dotnet/." "$DOTNET_HOME/"

    if [ -d "$STAGE/opt/microsoft/powershell/7" ]; then
        mkdir -p "$PWSH_HOME"
        cp -a "$STAGE/opt/microsoft/powershell/7/." "$PWSH_HOME/"
        chmod +x "$PWSH_HOME/pwsh" || true
    fi

    log "unpacked SDK $("$DOTNET_HOME/dotnet" --version) into $DOTNET_HOME"
fi

export DOTNET_ROOT="$DOTNET_HOME"
export PATH="$DOTNET_HOME:$PWSH_HOME:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
    {
        echo "export DOTNET_ROOT=\"$DOTNET_HOME\""
        echo "export PATH=\"$DOTNET_HOME:$PWSH_HOME:\$PATH\""
        echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
        echo 'export DOTNET_NOLOGO=1'
        echo 'export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
    } >> "$CLAUDE_ENV_FILE"
fi

# Warms ~/.nuget/packages, which the container image keeps. Avalonia and xunit.v3 are most of the
# restore, and paying for them once here is the difference between a first `dotnet test` that
# takes ten seconds and one that takes forty.
cd "${CLAUDE_PROJECT_DIR:-$(dirname "$(dirname "$(dirname "$(readlink -f "$0")")")")}"
log "restoring packages"
dotnet restore Uindosill.slnx || fail "restore failed"

log "ready: $(dotnet --version), $("$PWSH_HOME/pwsh" -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || echo 'pwsh unavailable')"
log "build with: dotnet build Uindosill.slnx -c Release"
log "test with:  dotnet test Uindosill.slnx -c Release"
