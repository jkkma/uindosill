<#
.SYNOPSIS
    Move run reports, research, session memory and test material between a machine and the
    maintainer's Drive over rclone, with every transfer verified by checksum.

.DESCRIPTION
    `runs/` is gitignored and machine-local, research never enters this repository at all, and the
    test audio is far too large for either — so the two machines meet on the maintainer's Drive.
    This is the route they meet by.

    **Why rclone and not a mounted drive.** Google Drive for desktop is a background application
    that mounts a drive letter, and the maintainer does not want it installed on the desktop. rclone
    is a single binary that talks to the same Drive over its API: no daemon, no local cache, the
    same command on both machines, and — the reason it earns its place here rather than merely
    working — `--checksum` compares hashes at both ends, so a transfer either matches or fails
    loudly. A sync daemon reports success when it has copied bytes; this reports success when the
    bytes agree.

    **No URL and no file id appears here, or in anything this prints.** This repository is public.
    Remote paths are folder *names* under a configured remote, which is all rclone needs, and the
    name `uindosill` is already public in `CLAUDE.md`. If a future edit wants to paste an id in to
    disambiguate something, the answer is to rename the folder instead.

    **One-time setup, per machine** — this cannot be scripted, because it ends in a browser:

        rclone config create gdrive drive scope=drive client_id=<id> client_secret=<secret>

    That opens Google's consent page and writes a refresh token into `rclone.conf` (under scoop:
    `~\scoop\persist\rclone\rclone.conf`). Confirm with `rclone listremotes`.

    **Two warnings that belong in the same breath as that command.** It prints the whole remote,
    token included, to the terminal — that output is a credential, so it does not get pasted
    anywhere, and if it has been, revoke rclone at `myaccount.google.com/permissions` and run the
    command again. And `client_id` is not optional in practice: omit it and rclone falls back to a
    shared id that Google is retiring, which rclone warns about on every call — "being retired and
    will stop working during 2026", observed 2026-08-16. Make an OAuth client of your own
    (rclone.org/drive/#making-your-own-client-id) so this does not stop working mid-measurement.

    `rclone.conf` never goes in this repository, which is why `-Remote` is a name here rather than
    anything carrying a secret.

.EXAMPLE
    # After a measuring session on the laptop: the run summaries go up for the other machine.
    .\scripts\sync-drive.ps1 -Runs laptop

.EXAMPLE
    # A research workflow's product — markdown, as written — goes to its dated folder.
    .\scripts\sync-drive.ps1 -Research .\out\diarisation-research-2026-08-16

.EXAMPLE
    # This machine's Claude Code session memory, to session-memory/<machine>. Push only; see the
    # route for why pulling it is a merge rather than a copy.
    .\scripts\sync-drive.ps1 -Memory desktop

.EXAMPLE
    # On the desktop: fetch the study before starting work against it.
    .\scripts\sync-drive.ps1 -Fetch diarisation-research-2026-08-16 -Destination .\research

.EXAMPLE
    # On the desktop: the four stratified test episodes, into the repository root, sizes checked.
    .\scripts\sync-drive.ps1 -Episodes

.EXAMPLE
    # What would move, without moving it.
    .\scripts\sync-drive.ps1 -Runs desktop -DryRun
#>

[CmdletBinding(DefaultParameterSetName = 'Runs')]
param(
    # Push runs/<machine>/ and runs/wer/ summaries to the Drive folder runs-<machine>.
    [Parameter(ParameterSetName = 'Runs', Mandatory)]
    [ValidateSet('laptop', 'desktop')]
    [string] $Runs,

    # Push a local research folder to a folder of the same name under uindosill/. Research is
    # markdown and travels as markdown; the next session pulls it with -Fetch and reads the files.
    [Parameter(ParameterSetName = 'Research', Mandatory)]
    [string] $Research,

    # Push this machine's Claude Code session memory to session-memory/<machine>. Push only:
    # the route explains why coming back the other way is a merge and not this script's job.
    [Parameter(ParameterSetName = 'Memory', Mandatory)]
    [ValidateSet('laptop', 'desktop')]
    [string] $Memory,

    # Pull a folder from under uindosill/ by name — a research folder, or runs-<machine>.
    [Parameter(ParameterSetName = 'Fetch', Mandatory)]
    [string] $Fetch,

    # Pull the four stratified diarisation test episodes from the Drive root.
    [Parameter(ParameterSetName = 'Episodes', Mandatory)]
    [switch] $Episodes,

    # Where a pull lands. Defaults to the repository root for -Episodes, and to a folder named
    # after the source for -Fetch.
    [Parameter(ParameterSetName = 'Fetch')]
    [Parameter(ParameterSetName = 'Episodes')]
    [string] $Destination,

    # The rclone remote name, as `rclone listremotes` reports it.
    [string] $Remote = 'gdrive',

    # The folder under the remote's root that holds this project's material.
    [string] $DriveFolder = 'uindosill',

    # Print what rclone would transfer and stop.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot

# The four episodes the maintainer supplied on 2026-08-16, one show, filenames carrying the
# speaker-count stratification the measurement plan asked for: nominally 2, 3, 5 and 7 voices.
# They live at the Drive root rather than under the project folder, which is where they were put.
$episodeNames = @(
    'two-hosts.mp3',
    'two-hosts-one-guest.mp3',
    'two-hosts-three-guests.mp3',
    'two-hosts-five-guests.mp3'
)

# ── rclone, and a remote that actually exists ───────────────────────────────────────────────────

$rclone = Get-Command rclone -ErrorAction SilentlyContinue
if (-not $rclone) {
    throw "rclone is not on PATH. Install it (scoop install rclone), then configure the remote:`n" +
          "  rclone config create $Remote drive scope=drive client_id=<id> client_secret=<secret>`n" +
          'That command prints a token — treat its output as a credential and do not paste it.'
}

$remotes = @(& $rclone.Source listremotes 2>&1)
if ($LASTEXITCODE -ne 0) { throw "rclone listremotes failed: $($remotes -join ' ')" }
$remotes = @($remotes | ForEach-Object { $_.TrimEnd(':') } | Where-Object { $_ })

if ($Remote -notin $remotes) {
    $known = if ($remotes) { $remotes -join ', ' } else { '(none configured)' }
    throw "No rclone remote named '$Remote'. Configured: $known.`n" +
          "Create it — this opens a browser once, and cannot be done for you:`n" +
          "  rclone config create $Remote drive scope=drive client_id=<id> client_secret=<secret>`n" +
          "Make the client id at rclone.org/drive/#making-your-own-client-id; the shared fallback is`n" +
          'being retired. That command prints a token — its output is a credential, do not paste it.'
}

$root = "${Remote}:${DriveFolder}"

# --checksum is the point of using rclone here: compare by hash, not by size and mtime, so a
# truncated or re-encoded file is a failure rather than a silent success.
#
# Not --progress: it repaints one line with carriage returns, which is unreadable the moment the
# output is redirected to a file or read by anything other than a terminal — and these runs get
# pasted into run reports. `-v` prints one durable line per file actually transferred, and
# --stats-one-line keeps the periodic summary to a single line.
$common = @('--checksum', '-v', '--stats-one-line', '--stats', '30s')
if ($DryRun) { $common += '--dry-run' }

function Invoke-Rclone {
    param([string[]] $Arguments, [string] $What)

    Write-Host ''
    Write-Host "── $What ─────────────────────────────────────" -ForegroundColor Green
    Write-Host ("  rclone {0}" -f ($Arguments -join ' ')) -ForegroundColor DarkGray

    & $rclone.Source @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$What failed (rclone exit $LASTEXITCODE)." }
}

function Assert-LocalPath([string] $Path, [string] $What) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "$What not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}

switch ($PSCmdlet.ParameterSetName) {

    'Runs' {
        # Summaries and their JSON, not the transcripts: the transcripts are large, regenerable and
        # already covered by CLAUDE.md's rule about what belongs in a run report.
        $source = Assert-LocalPath (Join-Path $repo 'runs') 'runs/'
        $target = "$root/runs-$Runs"

        Invoke-Rclone -What "runs/ → runs-$Runs" -Arguments (
            @('copy', $source, $target) + $common +
            @('--include', '**/summary.json', '--include', '**/summary.md', '--include', '**/*.md')
        )

        Write-Host ''
        Write-Host "  Keep runs-$Runs's README index current — including which working-tree changes are" -ForegroundColor Yellow
        Write-Host '  not yet pushed. Multi-MB artifacts do not travel: list how to regenerate them instead.' -ForegroundColor Yellow
    }

    'Research' {
        $source = Assert-LocalPath $Research 'research folder'
        $name = Split-Path -Leaf $source
        $target = "$root/$name"

        $documents = @(Get-ChildItem -LiteralPath $source -Filter *.md -File -Recurse)
        if ($documents.Count -eq 0) {
            throw "No markdown in $source. A research folder is the study as written; " +
                  'there is nothing here for the next session to read.'
        }

        Invoke-Rclone -What "$name → Drive" -Arguments (@('copy', $source, $target) + $common)
    }

    'Memory' {
        # Claude Code keeps its per-project memory outside the repository, under a key that is the
        # working copy's path with every ':' and '\' replaced by '-'. Derived from $repo rather
        # than written down: writing it down puts a username in a public repository, and the two
        # machines' paths differ anyway — which is the whole reason this route is per-machine.
        $slug = $repo -replace '[:\\]', '-'
        $profileRoot = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
        $source = Join-Path $profileRoot (Join-Path '.claude/projects' (Join-Path $slug 'memory'))

        if (-not (Test-Path -LiteralPath $source)) {
            throw "No session memory at $source.`n" +
                  'That folder appears once a Claude Code session has run in this working copy. ' +
                  'If one has, the key may differ: it is the working copy path with every '':'' ' +
                  'and ''\'' replaced by ''-''.'
        }
        $source = (Resolve-Path -LiteralPath $source).Path
        $target = "$root/session-memory/$Memory"

        # copy, not sync. The remote also holds the OTHER machine's memory, and files this machine
        # never had — on 2026-08-17 that folder carried one absent from the desktop entirely — so a
        # sync would delete them. Nothing up there is this machine's to remove.
        Invoke-Rclone -What "session memory → session-memory/$Memory" -Arguments (
            @('copy', $source, $target) + $common + @('--include', '*.md')
        )

        if ($DryRun) { break }

        Write-Host ''
        Write-Host '  Push only, deliberately. Installing these on the other machine is a MERGE, not a copy:' -ForegroundColor Yellow
        Write-Host '  MEMORY.md is one line per memory and each machine has entries the other does not, and a' -ForegroundColor Yellow
        Write-Host '  memory asserting which machine it was written on is false on the other one. Pull with' -ForegroundColor Yellow
        Write-Host '  -Fetch session-memory/<machine> into a scratch folder and merge by hand.' -ForegroundColor Yellow
    }

    'Fetch' {
        if (-not $Destination) { $Destination = Join-Path (Get-Location).Path (Split-Path -Leaf $Fetch) }
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        $Destination = (Resolve-Path -LiteralPath $Destination).Path

        Invoke-Rclone -What "$Fetch → $Destination" -Arguments (
            @('copy', "$root/$Fetch", $Destination) + $common
        )
    }

    'Episodes' {
        if (-not $Destination) { $Destination = $repo }
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        $Destination = (Resolve-Path -LiteralPath $Destination).Path

        $arguments = @('copy', "${Remote}:", $Destination) + $common
        foreach ($name in $episodeNames) { $arguments += @('--include', $name) }
        Invoke-Rclone -What 'test episodes → local' -Arguments $arguments

        if ($DryRun) { break }

        # Sizes read back from Drive and compared against what landed. rclone's --checksum has
        # already compared hashes; this prints the comparison so a session can say it checked
        # rather than assume, which is what the measurement plan asks for before the audio is used.
        Write-Host ''
        Write-Host '── episodes, against Drive metadata ────────────' -ForegroundColor Green

        # stderr is kept off stdout deliberately. rclone writes NOTICE lines there — the shared
        # client-id retirement warning, among others — and folding them in with 2>&1 puts prose in
        # front of the JSON, which then fails to parse for a reason that reads like a rclone bug
        # and is not one. --log-level ERROR silences the notices; the redirect keeps any real error
        # message available for the throw.
        $errorLog = [IO.Path]::GetTempFileName()
        try {
            $listing = & $rclone.Source lsjson "${Remote}:" --files-only --log-level ERROR 2> $errorLog
            if ($LASTEXITCODE -ne 0) {
                throw "rclone lsjson failed (exit $LASTEXITCODE): $((Get-Content -LiteralPath $errorLog -Raw).Trim())"
            }
        }
        finally {
            Remove-Item -LiteralPath $errorLog -Force -ErrorAction SilentlyContinue
        }
        $remoteFiles = ($listing -join "`n") | ConvertFrom-Json

        $bad = 0
        foreach ($name in $episodeNames) {
            $there = @($remoteFiles | Where-Object { $_.Name -eq $name }) | Select-Object -First 1
            $here = Join-Path $Destination $name

            if (-not $there) { Write-Host ("  {0,-28} not on the Drive root" -f $name) -ForegroundColor Red; $bad++; continue }
            if (-not (Test-Path -LiteralPath $here)) { Write-Host ("  {0,-28} did not arrive" -f $name) -ForegroundColor Red; $bad++; continue }

            $local = (Get-Item -LiteralPath $here).Length
            $ok = $local -eq [long] $there.Size
            if (-not $ok) { $bad++ }
            Write-Host ("  {0,-28} {1,13:N0} B  {2}" -f $name, $local, $(if ($ok) { 'matches Drive' } else { "DIFFERS from Drive's $([long]$there.Size) B" })) -ForegroundColor $(if ($ok) { 'Gray' } else { 'Red' })
        }

        if ($bad -gt 0) { throw "$bad episode(s) did not match. Do not measure against them." }
        Write-Host '  every episode matches the byte count Drive reports' -ForegroundColor Green
    }
}

Write-Host ''
if ($DryRun) { Write-Host 'Dry run: nothing was transferred.' -ForegroundColor Yellow }
