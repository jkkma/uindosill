<#
.SYNOPSIS
    Checks the actual vendored Windows libmpv before installer packing.
.DESCRIPTION
    A fresh PowerShell process uses the same absolute-path NativeLibrary.TryLoad overload as the
    product. The loaded Vulkan module must be the shipped sibling, not a runner SDK/driver copy.
    Checks the app's exported entry points, API major, options and player creation/initialization.
    No recording, window, GPU rendering or audio/video playback is exercised. Writes its JSON
    report under packaging/ by default, including ordinary failures (a process crash cannot do so).
#>
[CmdletBinding()]
param(
    [string] $NativeRoot,
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NativeRoot) { $NativeRoot = Join-Path $repoRoot 'native' }
if (-not $ReportPath) { $ReportPath = Join-Path $repoRoot 'packaging/mpv-smoke.json' }
$report = [ordered]@{ status = 'failed'; scope = 'load, exports, API, options, create and initialize; no playback' }

try {
    if (-not $IsWindows) { throw 'This check needs Windows and the actual x64 player DLLs.' }
    $library = [IO.Path]::GetFullPath((Join-Path $NativeRoot 'win-x64/mpv/libmpv-2.dll'))
    $loader = Join-Path (Split-Path -Parent $library) 'vulkan-1.dll'
    if (-not (Test-Path -LiteralPath $library) -or -not (Test-Path -LiteralPath $loader)) {
        throw 'The pinned libmpv and Vulkan loader must both be present.'
    }
    $source = Get-Content -Raw (Join-Path $repoRoot 'src/Parakeet.App/Services/Mpv/MpvNative.cs')
    $exports = @([regex]::Matches($source,
        '(?s)\[LibraryImport\((?<attribute>.*?)\)\]\s+internal static partial\s+\S+\s+(?<name>\w+)\(') |
        ForEach-Object {
            $entry = [regex]::Match($_.Groups['attribute'].Value, 'EntryPoint = "([^"]+)"')
            if ($entry.Success) { $entry.Groups[1].Value } else { $_.Groups['name'].Value }
        } | Sort-Object -Unique)
    if ($exports.Count -eq 0) { throw 'No product mpv imports were found.' }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class VendoredMpvCheck {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Api();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Initialize(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Destroy(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetOption(
        IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileNameW(
        IntPtr module, StringBuilder name, int length);

    static T Entry<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public static Dictionary<string, object> Run(string path, string loaderPath, string[] exports) {
        if (GetModuleHandleW("vulkan-1.dll") != IntPtr.Zero)
            throw new Exception("Vulkan was already loaded; run this check in a fresh pwsh process.");
        if (!NativeLibrary.TryLoad(path, out var library))
            throw new Exception("The product's absolute-path NativeLibrary.TryLoad could not load libmpv.");
        IntPtr core = IntPtr.Zero;
        try {
            var vulkan = GetModuleHandleW("vulkan-1.dll");
            var moduleName = new StringBuilder(32768);
            if (vulkan == IntPtr.Zero || GetModuleFileNameW(vulkan, moduleName, moduleName.Capacity) == 0)
                throw new Exception("The required Vulkan module was not loaded.");
            if (!String.Equals(Path.GetFullPath(moduleName.ToString()), Path.GetFullPath(loaderPath),
                               StringComparison.OrdinalIgnoreCase))
                throw new Exception("Vulkan came from outside the shipped player directory: " + moduleName);
            foreach (var name in exports) NativeLibrary.GetExport(library, name);
            uint api = Entry<Api>(library, "mpv_client_api_version")();
            if ((api >> 16) != 2) throw new Exception("The player requires libmpv API major 2.");
            core = Entry<Create>(library, "mpv_create")();
            if (core == IntPtr.Zero) throw new Exception("mpv_create returned null.");
            var set = Entry<SetOption>(library, "mpv_set_option_string");
            var options = new Dictionary<string, string> {
                ["config"] = "no", ["load-scripts"] = "no", ["osc"] = "no",
                ["input-default-bindings"] = "no", ["terminal"] = "no", ["ytdl"] = "no",
                ["vo"] = "libmpv", ["keep-open"] = "yes", ["idle"] = "yes",
                ["audio-display"] = "no", ["pause"] = "yes"
            };
            foreach (var option in options)
                if (set(core, option.Key, option.Value) < 0)
                    throw new Exception("The product option was rejected: " + option.Key);
            int initialized = Entry<Initialize>(library, "mpv_initialize")(core);
            if (initialized < 0) throw new Exception("mpv_initialize failed: " + initialized);
            return new Dictionary<string, object> {
                ["api"] = $"{api >> 16}.{api & 65535}", ["vulkanLoadedPath"] = moduleName.ToString(),
                ["exports"] = exports, ["options"] = options, ["initialized"] = true
            };
        } finally {
            if (core != IntPtr.Zero) Entry<Destroy>(library, "mpv_terminate_destroy")(core);
            NativeLibrary.Free(library);
        }
    }
}
'@
    $report.libraryPath = $library
    $report.librarySha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant()
    $report.vulkanSha256 = (Get-FileHash -LiteralPath $loader -Algorithm SHA256).Hash.ToLowerInvariant()
    $report.checks = [VendoredMpvCheck]::Run($library, $loader, [string[]]$exports)
    $report.status = 'passed'
} catch {
    $report.error = $_.Exception.ToString()
    throw
} finally {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
    $null = New-Item -ItemType Directory -Force -Path $parent
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
Write-Host 'Actual libmpv load, sibling Vulkan loader, exports, API and initialization passed.'
