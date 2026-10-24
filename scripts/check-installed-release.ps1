<#
.SYNOPSIS
    Install and uninstall both built Windows flavours on a disposable GitHub runner.
.DESCRIPTION
    Requires the release's Setup.exe and full nupkg files in packaging/releases. Uses private,
    unique --installto roots and a small scratch user-data directory, never the normal user data.
    Runs the real installer, the installed application's fast callback, and the real uninstaller.
    Compares installed metadata and executable/native hashes with each matching package.
    Writes packaging/installer-smoke.json and installer-smoke-*.log; temporary installs are removed.
    This checks installation and bootstrap, not recognition, GPU inference, playback or upgrades.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Commit,

    [string] $ReleaseDirectory = 'packaging/releases',
    [string] $Output = 'packaging/installer-smoke.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'This installer check requires a disposable Windows GitHub Actions runner.'
}

function Invoke-SmokeProcess([string] $Executable, [string[]] $ProcessArguments, [int] $TimeoutSeconds = 300) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $ProcessArguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    try {
        [void] $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
            $child.Kill($true)
            [void] $child.WaitForExit(30000)
            throw "$Executable did not exit within $TimeoutSeconds seconds."
        }
        $result = [ordered]@{
            exitCode = $child.ExitCode
            stdout = $stdout.GetAwaiter().GetResult()
            stderr = $stderr.GetAwaiter().GetResult()
        }
        if ($child.ExitCode -ne 0) {
            throw "$Executable exited $($child.ExitCode): $($result.stderr)"
        }
        return $result
    }
    finally { $child.Dispose() }
}

function Get-Metadata([xml] $Document) {
    $metadata = [ordered]@{}
    foreach ($field in 'id', 'version', 'channel', 'mainExe', 'rid') {
        $node = $Document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$field']")
        if ($null -eq $node) { throw "Package metadata is missing $field." }
        $metadata[$field] = $node.InnerText
    }
    return $metadata
}

function Get-EntryHash([IO.Compression.ZipArchive] $Archive, [string] $Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) { throw "Package is missing $Name." }
    $stream = $entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
$outputPath = [IO.Path]::GetFullPath($Output)
$evidenceRoot = [IO.Path]::GetDirectoryName($outputPath)
[void] [IO.Directory]::CreateDirectory($evidenceRoot)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('uindosill-install-smoke-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($scratch)
$previousDataRoot = $env:UINDOSILL_USER_DATA_DIR
$env:UINDOSILL_USER_DATA_DIR = Join-Path $scratch 'Uindosill'
[void] [IO.Directory]::CreateDirectory($env:UINDOSILL_USER_DATA_DIR)
$sentinel = Join-Path $env:UINDOSILL_USER_DATA_DIR 'keep.txt'
[IO.File]::WriteAllText($sentinel, 'Installer checks must preserve user data.')
$report = [ordered]@{ version = $Version; commit = $Commit; channels = @(); passed = $false }

try {
    foreach ($channel in 'win', 'win-cuda') {
        $suffix = if ($channel -eq 'win') { '' } else { '-win-cuda' }
        $installer = Join-Path $releaseRoot "UindosillDesktop-$channel-Setup.exe"
        $package = Join-Path $releaseRoot "UindosillDesktop-$Version$suffix-full.nupkg"
        foreach ($required in $installer, $package) {
            if (-not [IO.File]::Exists($required)) { throw "Missing release input: $required" }
        }
        $installRoot = Join-Path $scratch $channel
        $installLog = Join-Path $evidenceRoot "installer-smoke-$channel-install.log"
        $uninstallLog = Join-Path $evidenceRoot "installer-smoke-$channel-uninstall.log"
        $install = Invoke-SmokeProcess $installer @('--silent', '--installto', $installRoot, '--log', $installLog)
        $current = Join-Path $installRoot 'current'
        $app = Join-Path $current 'Uindosill.exe'
        $updater = Join-Path $installRoot 'Update.exe'
        $metadataPath = Join-Path $current 'sq.version'
        foreach ($required in $app, $updater, $metadataPath) {
            if (-not [IO.File]::Exists($required)) { throw "Installer did not create $required." }
        }
        $installed = Get-Metadata ([xml](Get-Content -Raw -LiteralPath $metadataPath))
        $expected = [ordered]@{
            id = 'UindosillDesktop'; version = $Version; channel = $channel
            mainExe = 'Uindosill.exe'; rid = 'win-x64'
        }
        foreach ($field in $expected.Keys) {
            if ($installed[$field] -ne $expected[$field]) {
                throw "Installed $field is '$($installed[$field])', expected '$($expected[$field])'."
            }
        }
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
        if (($productVersion -split '\+')[0] -ne $Version) {
            throw "Installed application reports $productVersion, expected $Version."
        }
        $archive = [IO.Compression.ZipFile]::OpenRead($package)
        $hashes = @()
        try {
            $files = @('Uindosill.exe', 'sq.version', 'native/win-x64/cpu/parakeet.dll',
                'native/win-x64/vulkan/parakeet.dll')
            if ($channel -eq 'win-cuda') { $files += 'native/win-x64/cuda/parakeet.dll' }
            elseif (Test-Path -LiteralPath (Join-Path $current 'native/win-x64/cuda')) {
                throw 'The standard installation unexpectedly contains the CUDA backend.'
            }
            foreach ($relative in $files) {
                $path = Join-Path $current $relative
                $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                $packageHash = Get-EntryHash $archive "lib/app/$relative"
                if ($actualHash -ne $packageHash) { throw "Installed $relative differs from its package." }
                $hashes += [ordered]@{ path = $relative; sha256 = $actualHash }
            }
        }
        finally { $archive.Dispose() }
        # The same managed fast callback Setup invokes; it exits before opening an Avalonia window.
        $bootstrap = Invoke-SmokeProcess $app @('--veloapp-install', $Version) 60
        $uninstall = Invoke-SmokeProcess $updater @('uninstall', '--silent', '--log', $uninstallLog) 120
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([IO.Directory]::Exists($installRoot) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if ([IO.Directory]::Exists($installRoot)) { throw "Uninstall left the private installation at $installRoot." }
        if ([IO.File]::ReadAllText($sentinel) -ne 'Installer checks must preserve user data.') {
            throw 'Installer or uninstall changed the user-data sentinel.'
        }
        $report.channels += [ordered]@{
            channel = $channel; metadata = $installed; productVersion = $productVersion
            installExitCode = $install.exitCode; bootstrapExitCode = $bootstrap.exitCode
            uninstallExitCode = $uninstall.exitCode; userDataPreserved = $true; hashes = $hashes
        }
        Write-Host "$channel installed, matched its package, bootstrapped, and uninstalled; user data preserved."
    }
    $report.passed = $true
}
catch {
    $report['error'] = $_.Exception.Message
    throw
}
finally {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding utf8
    $env:UINDOSILL_USER_DATA_DIR = $previousDataRoot
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
