<#
.SYNOPSIS
    Renders the application badge to brand/uindosill.ico and brand/uindosill-splash.png.

.DESCRIPTION
    The mark existed only as XAML geometry in MainWindow.axaml's headerbar, which is why every
    Windows surface outside the window — the taskbar, the desktop shortcut, Explorer, the Add/Remove
    Programs row, Setup.exe — showed the generic placeholder in v1.0.0-rc.3. Those surfaces want a
    real .ico file, so one is generated here from the same five-bar geometry and the same four
    colour tokens, and committed beside this script.

    Generated rather than drawn by hand, and committed rather than generated at build time: CI must
    not need a renderer, and the .ico has to be identical on every machine that builds an installer.
    Re-run this after changing the badge or the tokens it uses, and commit what changes.

    **No imaging library.** System.Drawing.Common is Windows-only, is not carried by every
    PowerShell 7 install, and would put a second thing to install into the release path. Everything
    below is arithmetic — signed distance fields for the rounded square and for each round-capped
    bar, one sample per pixel for the antialiasing — plus a PNG encoder over
    System.IO.Compression.ZLibStream, which .NET has had since 6. The output is 32-bit RGBA.

    **PNG-compressed ICO entries at every size.** Windows has read those since Vista; the BMP form
    below 256 is a Windows XP compatibility that costs four times the bytes.

.EXAMPLE
    .\scripts\make-icon.ps1
    Writes both files and prints their sizes. Takes a minute: the pixel loops are PowerShell.
#>

[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'brand' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# ── The design, in the badge's own 22-unit square ──────────────────────────────────────────────
#
# Taken from MainWindow.axaml: a 22x22 border, a hard matcha/taro seam down the middle, and the
# five-bar glyph laid out on the 18x17 box the path data spans, scaled Uniform into 13x13 and
# centred. The stroke is 2.4 in path units, so it scales with the glyph.
$matcha100 = @(0xE3, 0xEF, 0xD6)
$taro100   = @(0xEF, 0xE5, 0xFE)
$matcha700 = @(0x4A, 0x60, 0x2C)
$taro700   = @(0x61, 0x4B, 0x7C)
$ground    = @(0xFF, 0xFF, 0xFF)

# M4 11.5v1  M8.5 7v10  M13 3.5v17  M17.5 8.5v7  M22 11.5v1
$bars = @(
    @{ X = 4.0;    Y0 = 11.5; Y1 = 12.5 }
    @{ X = 8.5;    Y0 = 7.0;  Y1 = 17.0 }
    @{ X = 13.0;   Y0 = 3.5;  Y1 = 20.5 }
    @{ X = 17.5;   Y0 = 8.5;  Y1 = 15.5 }
    @{ X = 22.0;   Y0 = 11.5; Y1 = 12.5 }
)
$pathMinX = 4.0;  $pathMaxX = 22.0
$pathMinY = 3.5;  $pathMaxY = 20.5
$pathStroke = 2.4
$glyphBox = 13.0      # the Path's Width/Height inside the 22-unit badge
$badgeUnits = 22.0

function Get-RoundedSquareDistance([double] $px, [double] $py, [double] $half, [double] $radius) {
    # Signed distance to a rounded square centred on the origin. Negative inside.
    $qx = [math]::Abs($px) - ($half - $radius)
    $qy = [math]::Abs($py) - ($half - $radius)
    $ax = [math]::Max($qx, 0.0)
    $ay = [math]::Max($qy, 0.0)
    return [math]::Sqrt(($ax * $ax) + ($ay * $ay)) + [math]::Min([math]::Max($qx, $qy), 0.0) - $radius
}

function Get-CapsuleDistance(
    [double] $px, [double] $py,
    [double] $ax, [double] $ay, [double] $bx, [double] $by,
    [double] $radius) {

    # Signed distance to a round-capped segment: the same shape StrokeLineCap="Round" draws.
    $vx = $bx - $ax; $vy = $by - $ay
    $wx = $px - $ax; $wy = $py - $ay
    $lengthSquared = ($vx * $vx) + ($vy * $vy)

    $t = if ($lengthSquared -le 0.0) { 0.0 } else {
        [math]::Min(1.0, [math]::Max(0.0, (($wx * $vx) + ($wy * $vy)) / $lengthSquared))
    }

    $dx = $wx - ($vx * $t)
    $dy = $wy - ($vy * $t)
    return [math]::Sqrt(($dx * $dx) + ($dy * $dy)) - $radius
}

function Get-Coverage([double] $distance) {
    # One sample per pixel, with the edge softened over the pixel it falls in.
    return [math]::Min(1.0, [math]::Max(0.0, 0.5 - $distance))
}

function New-BadgeBitmap([int] $size, [bool] $opaqueGround) {
    <#
        RGBA bytes, row-major, for one square rendering of the badge at $size pixels.
        $opaqueGround paints the page colour behind it — wanted for the splash, not for an icon,
        whose corners have to be transparent or the taskbar draws a white tile.
    #>
    $pixels = New-Object 'byte[]' ($size * $size * 4)
    $scale = $size / $badgeUnits

    # The glyph's Uniform fit: the 18x17 path box into a 13x13 square, centred in the badge.
    $glyphScale = [math]::Min($glyphBox / ($pathMaxX - $pathMinX), $glyphBox / ($pathMaxY - $pathMinY))
    $glyphWidth = ($pathMaxX - $pathMinX) * $glyphScale
    $glyphHeight = ($pathMaxY - $pathMinY) * $glyphScale
    $glyphOriginX = ($badgeUnits - $glyphWidth) / 2.0
    $glyphOriginY = ($badgeUnits - $glyphHeight) / 2.0
    $strokeRadius = ($pathStroke * $glyphScale) / 2.0

    # Every bar in badge units, so the pixel loop does no layout arithmetic.
    $placed = @()
    foreach ($bar in $bars) {
        $placed += @{
            X  = $glyphOriginX + (($bar.X - $pathMinX) * $glyphScale)
            Y0 = $glyphOriginY + (($bar.Y0 - $pathMinY) * $glyphScale)
            Y1 = $glyphOriginY + (($bar.Y1 - $pathMinY) * $glyphScale)
        }
    }

    $half = $badgeUnits / 2.0
    $cornerRadius = $badgeUnits * 0.22
    $seam = $half

    for ($y = 0; $y -lt $size; $y++) {
        # Pixel centres, in badge units.
        $py = (($y + 0.5) / $scale)
        for ($x = 0; $x -lt $size; $x++) {
            $px = (($x + 0.5) / $scale)

            $squareDistance = Get-RoundedSquareDistance ($px - $half) ($py - $half) $half $cornerRadius
            $squareCoverage = Get-Coverage ($squareDistance * $scale)

            $glyphDistance = [double]::MaxValue
            foreach ($bar in $placed) {
                $d = Get-CapsuleDistance $px $py $bar.X $bar.Y0 $bar.X $bar.Y1 $strokeRadius
                if ($d -lt $glyphDistance) { $glyphDistance = $d }
            }
            $glyphCoverage = Get-Coverage ($glyphDistance * $scale)

            # The seam is hard by design — a gradient with both stops at 0.5 — and it splits the
            # centre bar down its own middle, which is why the glyph is tested against it too.
            $onLeft = $px -lt $seam
            $fill = if ($onLeft) { $matcha100 } else { $taro100 }
            $ink  = if ($onLeft) { $matcha700 } else { $taro700 }

            # Composite: ink over fill over (page or nothing).
            $baseAlpha = if ($opaqueGround) { 1.0 } else { $squareCoverage }
            $baseColour = if ($opaqueGround) {
                @(
                    ($ground[0] * (1.0 - $squareCoverage)) + ($fill[0] * $squareCoverage)
                    ($ground[1] * (1.0 - $squareCoverage)) + ($fill[1] * $squareCoverage)
                    ($ground[2] * (1.0 - $squareCoverage)) + ($fill[2] * $squareCoverage)
                )
            } else { @([double]$fill[0], [double]$fill[1], [double]$fill[2]) }

            # The glyph never paints outside the square: a round cap that overhangs the corner
            # radius would otherwise leave ink floating beside the badge.
            $inkCoverage = $glyphCoverage * $squareCoverage
            $alpha = [math]::Min(1.0, $baseAlpha + ($inkCoverage * (1.0 - $baseAlpha)))

            $r = ($baseColour[0] * (1.0 - $inkCoverage)) + ($ink[0] * $inkCoverage)
            $g = ($baseColour[1] * (1.0 - $inkCoverage)) + ($ink[1] * $inkCoverage)
            $b = ($baseColour[2] * (1.0 - $inkCoverage)) + ($ink[2] * $inkCoverage)

            $offset = (($y * $size) + $x) * 4
            $pixels[$offset]     = [byte][math]::Round([math]::Min(255.0, [math]::Max(0.0, $r)))
            $pixels[$offset + 1] = [byte][math]::Round([math]::Min(255.0, [math]::Max(0.0, $g)))
            $pixels[$offset + 2] = [byte][math]::Round([math]::Min(255.0, [math]::Max(0.0, $b)))
            $pixels[$offset + 3] = [byte][math]::Round(255.0 * $alpha)
        }
    }

    return $pixels
}

# ── PNG ────────────────────────────────────────────────────────────────────────────────────────

# Decimal, not hex, and that is not a style choice. PowerShell parses a hex literal into the
# smallest *signed* type whose bit pattern it matches, so 0xFFFFFFFF is [int] -1 and 0xEDB88320 is a
# negative [int] too — both of which then refuse to become a [uint32]. Writing the same two
# constants in decimal keeps them positive and keeps the arithmetic in the width CRC-32 is defined
# over.
$crcPolynomial = [uint32] 3988292384    # 0xEDB88320, reversed CRC-32
$crcSeed = [uint32] 4294967295          # 0xFFFFFFFF

$crcTable = New-Object 'uint32[]' 256
for ($n = 0; $n -lt 256; $n++) {
    [uint32] $c = [uint32] $n
    for ($k = 0; $k -lt 8; $k++) {
        if ($c -band 1) { $c = [uint32] ($crcPolynomial -bxor ($c -shr 1)) } else { $c = [uint32] ($c -shr 1) }
    }
    $crcTable[$n] = $c
}

function Get-Crc32([byte[]] $bytes) {
    [uint32] $c = $script:crcSeed
    foreach ($b in $bytes) {
        $c = [uint32] ($crcTable[[int](($c -bxor $b) -band 0xFF)] -bxor ($c -shr 8))
    }
    return [uint32] ($c -bxor $script:crcSeed)
}

function Get-BigEndian([uint32] $value) {
    return [byte[]] @(
        [byte](($value -shr 24) -band 0xFF)
        [byte](($value -shr 16) -band 0xFF)
        [byte](($value -shr 8) -band 0xFF)
        [byte]($value -band 0xFF)
    )
}

function New-PngChunk([string] $type, [byte[]] $data) {
    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($type)
    $body = $typeBytes + $data
    return (Get-BigEndian ([uint32] $data.Length)) + $body + (Get-BigEndian (Get-Crc32 $body))
}

function ConvertTo-Png([byte[]] $pixels, [int] $width, [int] $height) {
    # Filter byte 0 in front of every scanline: no prediction, which costs a little size and
    # removes a whole class of encoder bug from a file that has to be right the first time.
    $raw = New-Object 'byte[]' ($height * (1 + ($width * 4)))
    $stride = $width * 4
    for ($y = 0; $y -lt $height; $y++) {
        $rawOffset = $y * (1 + $stride)
        $raw[$rawOffset] = 0
        [System.Array]::Copy($pixels, $y * $stride, $raw, $rawOffset + 1, $stride)
    }

    $memory = New-Object System.IO.MemoryStream
    $deflate = New-Object System.IO.Compression.ZLibStream($memory, [System.IO.Compression.CompressionLevel]::SmallestSize, $true)
    try { $deflate.Write($raw, 0, $raw.Length) } finally { $deflate.Dispose() }
    $compressed = $memory.ToArray()
    $memory.Dispose()

    $header = (Get-BigEndian ([uint32] $width)) + (Get-BigEndian ([uint32] $height)) +
              [byte[]] @(8, 6, 0, 0, 0)   # 8 bits per channel, truecolour with alpha

    return [byte[]] @(137, 80, 78, 71, 13, 10, 26, 10) +
           (New-PngChunk 'IHDR' $header) +
           (New-PngChunk 'IDAT' $compressed) +
           (New-PngChunk 'IEND' ([byte[]] @()))
}

# ── ICO ────────────────────────────────────────────────────────────────────────────────────────

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

Write-Host 'Rendering the badge:' -ForegroundColor Cyan
$frames = @()
foreach ($size in $sizes) {
    Write-Host "   ${size}x${size}"
    $frames += ,(ConvertTo-Png (New-BadgeBitmap $size $false) $size $size)
}

$directory = New-Object System.Collections.Generic.List[byte]
$directory.AddRange([byte[]] @(0, 0, 1, 0))                       # reserved, type 1 (icon)
$directory.AddRange([System.BitConverter]::GetBytes([uint16] $sizes.Count))

# Every entry's payload starts after the directory, which is a fixed 6 + 16 bytes per image.
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    # 256 is written as 0: the field is one byte and the format has said so since it was invented.
    $dimension = if ($size -ge 256) { 0 } else { $size }
    $directory.AddRange([byte[]] @($dimension, $dimension, 0, 0))
    $directory.AddRange([System.BitConverter]::GetBytes([uint16] 1))    # colour planes
    $directory.AddRange([System.BitConverter]::GetBytes([uint16] 32))   # bits per pixel
    $directory.AddRange([System.BitConverter]::GetBytes([uint32] $frames[$i].Length))
    $directory.AddRange([System.BitConverter]::GetBytes([uint32] $offset))
    $offset += $frames[$i].Length
}

$icoPath = Join-Path $OutputDirectory 'uindosill.ico'
$stream = [System.IO.File]::Create($icoPath)
try {
    $bytes = $directory.ToArray()
    $stream.Write($bytes, 0, $bytes.Length)
    foreach ($frame in $frames) { $stream.Write($frame, 0, $frame.Length) }
}
finally { $stream.Dispose() }

Write-Host ("   uindosill.ico: {0:N0} bytes, $($sizes.Count) sizes" -f (Get-Item $icoPath).Length) -ForegroundColor Green

# ── The installer's splash ─────────────────────────────────────────────────────────────────────
#
# vpk --splashImage takes one image and shows it while Setup.exe unpacks. The mark alone, on the
# application's own ground colour: no wordmark, because rendering type here would mean carrying a
# font rasteriser into this script for one line of text that the window itself already says.

$splashWidth = 360
$splashHeight = 220
$markSize = 96

Write-Host 'Rendering the installer splash:' -ForegroundColor Cyan
$mark = New-BadgeBitmap $markSize $false

$splash = New-Object 'byte[]' ($splashWidth * $splashHeight * 4)
for ($i = 0; $i -lt ($splashWidth * $splashHeight); $i++) {
    $splash[($i * 4)]     = [byte] $ground[0]
    $splash[($i * 4) + 1] = [byte] $ground[1]
    $splash[($i * 4) + 2] = [byte] $ground[2]
    $splash[($i * 4) + 3] = 255
}

$markLeft = [int] (($splashWidth - $markSize) / 2)
$markTop = [int] (($splashHeight - $markSize) / 2)
for ($y = 0; $y -lt $markSize; $y++) {
    for ($x = 0; $x -lt $markSize; $x++) {
        $source = (($y * $markSize) + $x) * 4
        $alpha = $mark[$source + 3] / 255.0
        if ($alpha -le 0.0) { continue }

        $target = ((($markTop + $y) * $splashWidth) + ($markLeft + $x)) * 4
        for ($channel = 0; $channel -lt 3; $channel++) {
            $over = $mark[$source + $channel] * $alpha
            $under = $splash[$target + $channel] * (1.0 - $alpha)
            $splash[$target + $channel] = [byte][math]::Round($over + $under)
        }
    }
}

$splashPath = Join-Path $OutputDirectory 'uindosill-splash.png'
[System.IO.File]::WriteAllBytes($splashPath, (ConvertTo-Png $splash $splashWidth $splashHeight))
Write-Host ("   uindosill-splash.png: {0:N0} bytes, ${splashWidth}x${splashHeight}" -f (Get-Item $splashPath).Length) -ForegroundColor Green
