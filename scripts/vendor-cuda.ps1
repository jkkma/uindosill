<#
.SYNOPSIS
    Verifies, unpacks and inspects the two parakeet.cpp CUDA archives into native/win-x64/cuda/.

.DESCRIPTION
    CUDA is the one backend that arrives as two archives — the library and a separate CUDA runtime
    — and the one where a wrong drop fails *silently*. A CUDA library that cannot be loaded is
    indistinguishable from one that is not there: the loader moves on, and for a CUDA request the
    next backend is CPU (ParakeetNativeLibrary.BackendOrder skips Vulkan when CUDA was asked for),
    so the run completes with a correct transcript and no error anywhere. Everything here exists to
    make that impossible to mistake for success.

    Three questions get answered before anything is run:

    1. Did I receive the archives upstream serves? SHA-256 of each, printed for the digest table in
       docs/NATIVE-BINARIES.md.
    2. What is actually in them? The full file list, because docs/NATIVE-BINARIES.md records the
       CPU and Vulkan archives as a single self-contained parakeet.dll and the CUDA pair is a
       different shape.
    3. Which GPU architectures were compiled in? An RTX 5080 is Blackwell, compute capability 12.0
       / sm_120, and ggml needs CUDA Toolkit 12.8+ to emit sm_120 code. Upstream builds Windows
       binaries in a release job with no CI and records no toolkit version, so this is unknown
       until someone looks. -SkipArchScan turns the scan off.

    The architecture scan walks NVIDIA fat binary headers embedded in the PE. Entry headers are not
    compressed even when their payloads are, so the architecture list is readable without the CUDA
    toolkit installed. Every container is validated before its contents are believed, and anything
    that fails validation is reported as unparsed rather than guessed at — a wrong architecture
    list is worse than no architecture list.

.EXAMPLE
    .\scripts\vendor-cuda.ps1 -LibArchive .\parakeet-v0.5.0-lib-win-cuda-x64.zip `
                              -CudartArchive .\cudart-parakeet-bin-win-cuda-x64.zip

.EXAMPLE
    # Inspect a drop that is already in place, without unpacking anything.
    .\scripts\vendor-cuda.ps1 -InspectOnly
#>

[CmdletBinding()]
param(
    # The library archive: parakeet-v0.5.0-lib-win-cuda-x64.zip (~149 MB).
    [string] $LibArchive,

    # The CUDA runtime archive: cudart-parakeet-bin-win-cuda-x64.zip (~553 MB).
    [string] $CudartArchive,

    # Where the flattened contents land. Defaults to the layout the loader searches.
    [string] $Destination,

    # Inspect what is already in -Destination; unpack nothing.
    [switch] $InspectOnly,

    # Overwrite files already present in -Destination.
    [switch] $Force,

    # Skip the fat binary architecture scan (it reads each DLL in full).
    [switch] $SkipArchScan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Destination) {
    $Destination = Join-Path $repo 'native/win-x64/cuda'
}

function Write-Heading {
    param([string] $Text)
    Write-Host ''
    Write-Host ("── $Text " + ('─' * [Math]::Max(1, 46 - $Text.Length))) -ForegroundColor Green
}

# ── NVIDIA fat binary walker ────────────────────────────────────────────────────────────────────
#
# A CUDA binary embeds one or more fat binary containers. Each is a 16-byte header followed by
# entries; each entry header carries the SM architecture it was compiled for, and payloads may be
# compressed but headers are not. Layout, all little-endian:
#
#   container   0x00 u32 magic = 0xBA55ED50
#               0x04 u16 version = 1
#               0x06 u16 headerSize = 16
#               0x08 u64 size of all entries that follow
#
#   entry       0x00 u16 kind          1 = PTX, 2 = ELF (cubin)
#               0x02 u16 version
#               0x04 u32 headerSize
#               0x08 u64 padded payload size
#               0x1C u32 sm version    120 = sm_120
#               0x20 u32 bit width     64
#
# The layout is not published by NVIDIA, so nothing here is believed without checking: a container
# whose entries do not walk exactly to its stated end, or that yields an implausible architecture
# or bit width, is discarded whole and counted as unparsed.
$scannerSource = @'
using System;
using System.Collections.Generic;
using System.IO;

public class FatbinScanner
{
    public class Entry
    {
        public int Kind;
        public int SmVersion;
        public long Offset;
    }

    public class Result
    {
        public List<Entry> Entries = new List<Entry>();
        public int ContainersParsed;
        public int ContainersRejected;
        public long Length;
    }

    private static bool PlausibleSm(int sm)
    {
        // Real compute capabilities only: 3.0 through 12.x, expressed as major*10 + minor.
        // Blackwell consumer is 120. Anything outside this is a misparse, not a new GPU.
        if (sm < 30 || sm > 129) return false;
        int minor = sm % 10;
        return minor <= 9;
    }

    public static Result Scan(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Result result = new Result();
        result.Length = data.LongLength;

        for (long i = 0; i + 16 <= data.LongLength; i++)
        {
            if (data[i] != 0x50 || data[i + 1] != 0xED || data[i + 2] != 0x55 || data[i + 3] != 0xBA)
                continue;

            int version = BitConverter.ToUInt16(data, (int)(i + 4));
            int headerSize = BitConverter.ToUInt16(data, (int)(i + 6));
            long fatSize = BitConverter.ToInt64(data, (int)(i + 8));

            if (version != 1 || headerSize != 16 || fatSize <= 0 || i + 16 + fatSize > data.LongLength)
            {
                result.ContainersRejected++;
                continue;
            }

            long cursor = i + 16;
            long end = cursor + fatSize;
            List<Entry> found = new List<Entry>();
            bool ok = true;

            while (cursor < end)
            {
                if (cursor + 0x24 > data.LongLength) { ok = false; break; }

                int kind = BitConverter.ToUInt16(data, (int)cursor);
                int entryHeaderSize = BitConverter.ToInt32(data, (int)(cursor + 4));
                long payload = BitConverter.ToInt64(data, (int)(cursor + 8));
                int sm = BitConverter.ToInt32(data, (int)(cursor + 0x1C));
                int bits = BitConverter.ToInt32(data, (int)(cursor + 0x20));

                if ((kind != 1 && kind != 2) ||
                    entryHeaderSize < 0x24 || entryHeaderSize > 0x400 ||
                    payload < 0 ||
                    (bits != 32 && bits != 64) ||
                    !PlausibleSm(sm))
                {
                    ok = false;
                    break;
                }

                Entry e = new Entry();
                e.Kind = kind;
                e.SmVersion = sm;
                e.Offset = cursor;
                found.Add(e);

                cursor += entryHeaderSize + payload;
            }

            // The entries must tile the container exactly. Landing short or long means the walk
            // was reading something that merely started with the magic number.
            if (!ok || cursor != end || found.Count == 0)
            {
                result.ContainersRejected++;
                continue;
            }

            result.ContainersParsed++;
            result.Entries.AddRange(found);
            i = end - 1;
        }

        return result;
    }
}
'@

# ── PE import table reader ──────────────────────────────────────────────────────────────────────
#
# Which DLLs parakeet.dll asks Windows for by name, so a missing dependency is a named file rather
# than "the library failed to load". Windows resolves these from the loaded module's own directory
# first (the loader passes an absolute path), so everything listed here has to sit in the same
# directory as the DLL that imports it.
$importSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class PeImports
{
    public static List<string> Read(string path)
    {
        List<string> names = new List<string>();
        byte[] d = File.ReadAllBytes(path);

        if (d.Length < 0x40 || d[0] != 0x4D || d[1] != 0x5A) return names;
        int pe = BitConverter.ToInt32(d, 0x3C);
        if (pe <= 0 || pe + 24 > d.Length) return names;
        if (BitConverter.ToInt32(d, pe) != 0x00004550) return names;

        int numberOfSections = BitConverter.ToUInt16(d, pe + 6);
        int sizeOfOptionalHeader = BitConverter.ToUInt16(d, pe + 20);
        int optional = pe + 24;
        int magic = BitConverter.ToUInt16(d, optional);
        bool pe32Plus = magic == 0x20B;
        int dataDirectory = optional + (pe32Plus ? 112 : 96);

        // Directory entry 1 is the import table.
        int importRva = BitConverter.ToInt32(d, dataDirectory + 8);
        if (importRva == 0) return names;

        int sectionTable = optional + sizeOfOptionalHeader;
        int[] va = new int[numberOfSections];
        int[] size = new int[numberOfSections];
        int[] raw = new int[numberOfSections];
        for (int s = 0; s < numberOfSections; s++)
        {
            int e = sectionTable + s * 40;
            if (e + 40 > d.Length) return names;
            size[s] = BitConverter.ToInt32(d, e + 8);
            va[s] = BitConverter.ToInt32(d, e + 12);
            raw[s] = BitConverter.ToInt32(d, e + 20);
        }

        int table = ToOffset(importRva, va, size, raw, numberOfSections);
        if (table < 0) return names;

        for (int i = 0; ; i++)
        {
            int descriptor = table + i * 20;
            if (descriptor + 20 > d.Length) break;

            int nameRva = BitConverter.ToInt32(d, descriptor + 12);
            int thunk = BitConverter.ToInt32(d, descriptor);
            if (nameRva == 0 && thunk == 0) break;

            int nameOffset = ToOffset(nameRva, va, size, raw, numberOfSections);
            if (nameOffset < 0) break;

            StringBuilder sb = new StringBuilder();
            for (int c = nameOffset; c < d.Length && d[c] != 0; c++) sb.Append((char)d[c]);
            if (sb.Length > 0) names.Add(sb.ToString());
        }

        return names;
    }

    private static int ToOffset(int rva, int[] va, int[] size, int[] raw, int count)
    {
        for (int s = 0; s < count; s++)
        {
            if (rva >= va[s] && rva < va[s] + size[s]) return raw[s] + (rva - va[s]);
        }
        return -1;
    }
}
'@

function Get-ArchiveDigest {
    param([string] $Path, [string] $Label)

    $item = Get-Item -LiteralPath $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Host ("{0,-14} {1}" -f 'archive', $item.Name)
    Write-Host ("{0,-14} {1:N0} bytes ({2:N1} MB)" -f 'size', $item.Length, ($item.Length / 1MB))
    Write-Host ("{0,-14} {1}" -f 'sha-256', $hash)

    return [PSCustomObject]@{
        Label  = $Label
        Name   = $item.Name
        Length = $item.Length
        Sha256 = $hash
        Path   = $item.FullName
    }
}

function Expand-Flattened {
    param([string] $Path, [string] $Target, [switch] $Overwrite)

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    $written = 0
    $skipped = 0
    $collisions = @()

    try {
        foreach ($entry in $zip.Entries) {
            # Flatten: upstream archives wrap their contents in a directory named after the
            # archive, and the loader looks for parakeet.dll directly inside the backend folder.
            if (-not $entry.Name) { continue }

            $out = Join-Path $Target $entry.Name
            if ((Test-Path -LiteralPath $out) -and -not $Overwrite) {
                $existing = Get-Item -LiteralPath $out
                if ($existing.Length -ne $entry.Length) {
                    $collisions += [PSCustomObject]@{
                        Name     = $entry.Name
                        OnDisk   = $existing.Length
                        InArchive = $entry.Length
                    }
                }
                $skipped++
                continue
            }

            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
            $written++
        }
    }
    finally {
        $zip.Dispose()
    }

    Write-Host ("{0,-14} {1} written, {2} already present" -f 'extracted', $written, $skipped)

    if ($collisions.Count -gt 0) {
        Write-Host ''
        Write-Host 'Files already on disk differ in size from the archive. Re-run with -Force to overwrite:' -ForegroundColor Yellow
        $collisions | Format-Table -AutoSize | Out-String | Write-Host
    }
}

function Get-ZipListing {
    param([string] $Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($zip.Entries | Where-Object { $_.Name } | ForEach-Object {
            [PSCustomObject]@{
                Name   = $_.FullName
                Length = $_.Length
            }
        })
    }
    finally {
        $zip.Dispose()
    }
}

Push-Location $repo
try {
    if (-not $InspectOnly) {
        if (-not $LibArchive -or -not $CudartArchive) {
            throw 'Both -LibArchive and -CudartArchive are required unless -InspectOnly is given.'
        }

        foreach ($archive in @($LibArchive, $CudartArchive)) {
            if (-not (Test-Path -LiteralPath $archive)) {
                throw "Archive not found: $archive"
            }
        }

        Write-Heading 'archives'
        $digests = @()
        $digests += Get-ArchiveDigest -Path $LibArchive -Label 'lib'
        Write-Host ''
        $digests += Get-ArchiveDigest -Path $CudartArchive -Label 'cudart'

        Write-Heading 'archive contents'
        foreach ($digest in $digests) {
            Write-Host $digest.Name -ForegroundColor Cyan
            $listing = Get-ZipListing -Path $digest.Path
            foreach ($entry in ($listing | Sort-Object Name)) {
                Write-Host ("  {0,-52} {1,14:N0} bytes" -f $entry.Name, $entry.Length)
            }
            Write-Host ("  {0} files" -f $listing.Count)
            Write-Host ''
        }

        New-Item -ItemType Directory -Path $Destination -Force | Out-Null

        Write-Heading 'unpacking'
        Write-Host ("destination    {0}" -f $Destination)
        foreach ($digest in $digests) {
            Write-Host ''
            Write-Host $digest.Name -ForegroundColor Cyan
            Expand-Flattened -Path $digest.Path -Target $Destination -Overwrite:$Force
        }

        Write-Heading 'digest table row'
        Write-Host 'Paste these into the table in docs/NATIVE-BINARIES.md:'
        Write-Host ''
        foreach ($digest in $digests) {
            Write-Host ("| v0.5.0 | ``{0}`` | ``{1}`` | | |" -f $digest.Name, $digest.Sha256)
        }
    }

    if (-not (Test-Path -LiteralPath $Destination)) {
        throw "Nothing at $Destination. Pass -LibArchive and -CudartArchive to unpack there."
    }

    $files = @(Get-ChildItem -LiteralPath $Destination -File | Sort-Object Name)

    Write-Heading 'what is in native/win-x64/cuda'
    $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    foreach ($file in $files) {
        $version = ''
        try {
            if ($file.VersionInfo -and $file.VersionInfo.FileVersion) {
                $version = $file.VersionInfo.FileVersion.Trim()
            }
        }
        catch {
            # Not every PE carries a version resource, and none of them have to.
        }
        Write-Host ("  {0,-40} {1,14:N0} bytes  {2}" -f $file.Name, $file.Length, $version)
    }
    Write-Host ("  {0} files, {1:N0} MB total" -f $files.Count, ($totalBytes / 1MB))

    # The CUDA runtime's own file version is the toolkit that produced this drop, and the toolkit
    # is what decides whether sm_120 code exists at all: ggml cannot emit it before 12.8.
    $runtime = @($files | Where-Object { $_.Name -like 'cudart64_*.dll' })
    if ($runtime.Count -gt 0) {
        Write-Heading 'CUDA runtime version'
        foreach ($dll in $runtime) {
            $reported = 'no version resource'
            if ($dll.VersionInfo -and $dll.VersionInfo.FileVersion) {
                $reported = $dll.VersionInfo.FileVersion.Trim()
            }
            Write-Host ("  {0,-40} {1}" -f $dll.Name, $reported)
        }
        Write-Host ''
        Write-Host '  CUDA Toolkit 12.8 is the first release that can emit sm_120 (Blackwell, RTX 50xx).' -ForegroundColor Yellow
        Write-Host '  A runtime older than 12.8 here means the library was almost certainly built with' -ForegroundColor Yellow
        Write-Host '  a toolkit that had no sm_120 target. The scan below is the direct evidence.' -ForegroundColor Yellow
    }

    $parakeet = @($files | Where-Object { $_.Name -eq 'parakeet.dll' })
    if ($parakeet.Count -eq 1) {
        Write-Heading 'parakeet.dll imports'
        try {
            Add-Type -TypeDefinition $importSource -Language CSharp -ErrorAction Stop | Out-Null
        }
        catch {
            # Already added by an earlier run in this session.
        }

        $imports = [PeImports]::Read($parakeet[0].FullName)
        $present = @($files | ForEach-Object { $_.Name.ToLowerInvariant() })
        $missing = @()

        foreach ($import in ($imports | Sort-Object)) {
            $isSystem = $import -match '^(api-ms-|ext-ms-|kernel32|user32|advapi32|msvcrt|ucrtbase|vcruntime|msvcp|ole32|oleaut32|shell32|shlwapi|ws2_32|bcrypt|crypt32|dbghelp|setupapi|cfgmgr32|powrprof|winmm|gdi32|version|rpcrt4|secur32|ntdll|combase|nvcuda)'
            $found = $present -contains $import.ToLowerInvariant()
            if ($found) {
                Write-Host ("  {0,-40} present in this directory" -f $import) -ForegroundColor Green
            }
            elseif ($isSystem) {
                Write-Host ("  {0,-40} system / driver" -f $import)
            }
            else {
                Write-Host ("  {0,-40} NOT FOUND" -f $import) -ForegroundColor Red
                $missing += $import
            }
        }

        if ($missing.Count -gt 0) {
            Write-Host ''
            Write-Host 'A missing import makes LoadLibrary fail, and the loader treats that as "this backend' -ForegroundColor Red
            Write-Host 'is not here" and moves on to CPU without saying so. Put these files in the same' -ForegroundColor Red
            Write-Host 'directory before drawing any conclusion about CUDA.' -ForegroundColor Red
        }
    }

    if (-not $SkipArchScan) {
        Write-Heading 'compiled GPU architectures'
        try {
            Add-Type -TypeDefinition $scannerSource -Language CSharp -ErrorAction Stop | Out-Null
        }
        catch {
            # Already added by an earlier run in this session.
        }

        $candidates = @($files | Where-Object { $_.Extension -eq '.dll' -and $_.Length -gt 1MB })
        $anyParsed = $false
        $sawSm120 = $false

        foreach ($file in $candidates) {
            $scan = [FatbinScanner]::Scan($file.FullName)
            if ($scan.ContainersParsed -eq 0) {
                if ($scan.ContainersRejected -gt 0) {
                    Write-Host ("  {0,-40} {1} fat binary header(s), none parsed" -f $file.Name, $scan.ContainersRejected) -ForegroundColor Yellow
                }
                continue
            }

            $anyParsed = $true
            $cubin = @($scan.Entries | Where-Object { $_.Kind -eq 2 } | ForEach-Object { $_.SmVersion } | Sort-Object -Unique)
            $ptx = @($scan.Entries | Where-Object { $_.Kind -eq 1 } | ForEach-Object { $_.SmVersion } | Sort-Object -Unique)

            Write-Host ("  {0}" -f $file.Name) -ForegroundColor Cyan
            Write-Host ("    containers   {0} parsed, {1} rejected" -f $scan.ContainersParsed, $scan.ContainersRejected)
            if ($cubin.Count -gt 0) {
                Write-Host ("    cubin        {0}" -f (($cubin | ForEach-Object { "sm_$_" }) -join ', '))
            }
            else {
                Write-Host '    cubin        none'
            }
            if ($ptx.Count -gt 0) {
                Write-Host ("    ptx          {0}" -f (($ptx | ForEach-Object { "compute_$_" }) -join ', '))
            }
            else {
                Write-Host '    ptx          none'
            }

            if ($cubin -contains 120) { $sawSm120 = $true }
        }

        Write-Host ''
        if (-not $anyParsed) {
            Write-Host 'No fat binary container validated. This scan proves nothing either way — it does not' -ForegroundColor Yellow
            Write-Host 'establish that sm_120 is absent. Run the transcription and read the result instead.' -ForegroundColor Yellow
        }
        elseif ($sawSm120) {
            Write-Host 'sm_120 cubin present: this library has native Blackwell kernels and should run on an' -ForegroundColor Green
            Write-Host 'RTX 5080 with no JIT cost.' -ForegroundColor Green
        }
        else {
            Write-Host 'No sm_120 cubin. An RTX 5080 is compute capability 12.0, so every kernel has to come' -ForegroundColor Yellow
            Write-Host 'from PTX through the driver JIT — a long first run and a warm cache after it — or fail' -ForegroundColor Yellow
            Write-Host 'with "no kernel image is available for execution on the device" if no PTX is embedded' -ForegroundColor Yellow
            Write-Host 'either. Both are measurable; see docs/UNPROVEN.md.' -ForegroundColor Yellow
        }
    }

    Write-Heading 'next'
    Write-Host '  dotnet build src/Parakeet.Cli -c Release        (copies native/ into the build output)'
    Write-Host '  uindosill doctor                                (does the DLL and its dependencies resolve)'
    Write-Host '  .\scripts\measure-transcribe.ps1 -Path chunk.m4a -Backend cuda'
    Write-Host ''
    Write-Host '  doctor only calls parakeet_capi_abi_version, which touches no CUDA state, so it reports'
    Write-Host '  ok for any library that loads — including one with no kernels for this GPU. Read its'
    Write-Host '  "from <path>" to confirm the file came from the cuda directory, then measure.'
    Write-Host ''
    Write-Host '  The "on <backend>" line and the JSON "backend" field report the backend that LOADED.'
    Write-Host '  A CUDA request falls back to CPU, never Vulkan, so "on cpu" at RTF near 0.08 is the'
    Write-Host '  shape of a CUDA library that did not load.'
}
finally {
    Pop-Location
}
