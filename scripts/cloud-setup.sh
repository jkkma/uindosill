#!/bin/bash
# PASTE THIS INTO THE "SETUP SCRIPT" FIELD OF THE CLAUDE CODE CLOUD ENVIRONMENT SETTINGS.
#
# It is not run from the clone. It lives here so the text is versioned and findable rather than
# living only in somebody's clipboard.
#
# Installs .NET SDK 10.0.400 and PowerShell 7.6.4 so a cloud session can build and test.
#
# Not the vendor's installer: dot.net redirects to builds.dotnet.microsoft.com, which the network
# policy there refuses with a 403 at CONNECT, as do download.visualstudio.microsoft.com,
# dotnetbuilds.azureedge.net and ci.dot.net. packages.microsoft.com is allowed and serves the same
# binaries as .deb archives, so they are unpacked rather than installed.
#
# Not the Ubuntu feed either, though the image is Ubuntu: Microsoft's ubuntu/24.04/prod carries no
# .NET 10 at all, its newest dotnet package being 6.0. The Debian 12 build runs correctly there.
#
# Versions are pinned and every download is checked against the SHA-256 the feed publishes, for the
# same reason NATIVE-BINARIES.md in the project notes pins a parakeet.cpp release. When a newer SDK is wanted, the
# current list is at:
#   https://packages.microsoft.com/debian/12/prod/dists/bookworm/main/binary-amd64/Packages.gz
#
# Installs to /opt with symlinks in /usr/local/bin and a /etc/profile.d entry, rather than to
# $HOME, because the toolchain has to be found by a later session's shell with nothing sourced.
# That is the difference that made an earlier attempt appear to work and then not be there.
#
# This is the only thing that sets a session up. A SessionStart hook was tried and removed: it
# could not take effect until it was on the default branch, so it did nothing for the case it was
# written for — a fresh session opened before then — and it installed a second copy of the same
# toolchain on top of this one. One mechanism, in the one place that runs early enough.

set -euo pipefail

DOTNET_HOME=/opt/dotnet
PWSH_HOME=/opt/powershell
FEED=https://packages.microsoft.com/debian/12/prod

if [ -x "$DOTNET_HOME/dotnet" ] && "$DOTNET_HOME/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.0\.400 '; then
    echo "[setup] SDK 10.0.400 already present"
else
    echo "[setup] fetching the toolchain"
    CACHE=$(mktemp -d)
    STAGE=$(mktemp -d)
    trap 'rm -rf "$CACHE" "$STAGE"' EXIT

    while read -r sha path; do
        [ -n "${sha:-}" ] || continue
        file="$CACHE/$(basename "$path")"
        curl -fsSL --retry 3 --retry-delay 2 --max-time 900 -o "$file" "$FEED/$path"
        actual=$(sha256sum "$file" | cut -d' ' -f1)
        # Never unpack a toolchain that is not the one pinned: a build that behaves differently
        # for no visible reason is the failure this check exists to prevent.
        [ "$actual" = "$sha" ] || { echo "[setup] DIGEST MISMATCH for $path: expected $sha, got $actual" >&2; exit 1; }
        if command -v dpkg-deb >/dev/null 2>&1; then
            dpkg-deb -x "$file" "$STAGE"
        else
            (cd "$CACHE" && ar x "$file" && tar xf data.tar.* -C "$STAGE" && rm -f data.tar.* control.tar.* debian-binary)
        fi
    done <<'PACKAGES'
ba4047cfe4ac6bb6c8cc7bd66725ba3c5d7dca237d61dc2fa651322f1c82d642 pool/main/d/dotnet-host/dotnet-host_10.0.11-1_amd64.deb
3d019e677ea2c976246df262a064def729d64e9338b1abd3ba2e7cec36937a3b pool/main/d/dotnet-hostfxr-10.0/dotnet-hostfxr-10.0_10.0.11-1_amd64.deb
2fe21a581d608e1370367b0daf52136dc379febe4662cbf4e1599edb85822580 pool/main/d/dotnet-runtime-10.0/dotnet-runtime-10.0_10.0.11-1_amd64.deb
19ed2ea510143eac785b513f6e987f909c5a436c527b645417df8807985a1488 pool/main/d/dotnet-targeting-pack-10.0/dotnet-targeting-pack-10.0_10.0.11-1_amd64.deb
b00712332658ba461cb1eb7a187251734486d1cff9d54a8c41d44b7c6cee8269 pool/main/d/dotnet-apphost-pack-10.0/dotnet-apphost-pack-10.0_10.0.11-1_amd64.deb
0f12001d1918f7ad2452d14d70bd396c82080b691407735213e90de637061f57 pool/main/n/netstandard-targeting-pack-2.1/netstandard-targeting-pack-2.1_2.1.0-1_amd64.deb
e42c102495d7f4813880a7c29fa02f1f58544e3a08099968c48858e218b1b6c8 pool/main/d/dotnet-sdk-10.0/dotnet-sdk-10.0_10.0.400-1_amd64.deb
e5688e0569568d48051c49d3e93504cde47af709cdaaabd9a8892bc676b3bdf3 pool/main/p/powershell/powershell_7.6.4-1.deb_amd64.deb
PACKAGES

    mkdir -p "$DOTNET_HOME"
    cp -a "$STAGE/usr/share/dotnet/." "$DOTNET_HOME/"
    if [ -d "$STAGE/opt/microsoft/powershell/7" ]; then
        mkdir -p "$PWSH_HOME"
        cp -a "$STAGE/opt/microsoft/powershell/7/." "$PWSH_HOME/"
        chmod +x "$PWSH_HOME/pwsh"
    fi
fi

# On PATH for every later shell. The symlink is what makes `dotnet` work in a bare shell; the
# profile script is what makes DOTNET_ROOT right for anything that looks it up rather than
# resolving it from the executable's own path.
ln -sf "$DOTNET_HOME/dotnet" /usr/local/bin/dotnet
[ -x "$PWSH_HOME/pwsh" ] && ln -sf "$PWSH_HOME/pwsh" /usr/local/bin/pwsh

cat > /etc/profile.d/dotnet.sh <<EOF
export DOTNET_ROOT="$DOTNET_HOME"
export PATH="\$PATH:$DOTNET_HOME:$PWSH_HOME"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
EOF
chmod +x /etc/profile.d/dotnet.sh

# Belt and braces: not every non-interactive shell reads profile.d.
for rc in "$HOME/.bashrc" /root/.bashrc; do
    [ -f "$rc" ] || continue
    grep -q 'profile.d/dotnet.sh' "$rc" || echo '. /etc/profile.d/dotnet.sh' >> "$rc"
done

. /etc/profile.d/dotnet.sh

# Warm the NuGet cache if the clone is already here. Avalonia and xunit.v3 are most of a restore,
# and the container image keeps ~/.nuget/packages.
for candidate in /home/user/uindosill "${CLAUDE_PROJECT_DIR:-}" "$PWD"; do
    if [ -n "$candidate" ] && [ -f "$candidate/Uindosill.slnx" ]; then
        echo "[setup] restoring packages in $candidate"
        (cd "$candidate" && dotnet restore Uindosill.slnx) || echo "[setup] restore failed; the SDK is still installed"
        break
    fi
done

echo "[setup] ready: $(dotnet --version), pwsh $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || echo 'unavailable')"
