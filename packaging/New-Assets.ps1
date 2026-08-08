<#
.SYNOPSIS
    Generates the MSIX visual asset set from the application icon.

.DESCRIPTION
    MSIX wants the same logo at roughly forty sizes: one per tile shape, per
    display scale factor, plus the target sizes Windows uses for the taskbar and
    the Start list. Hand-cutting those is tedious and drifts the moment the icon
    changes, so they are generated from a single 256px source instead.

    Tile assets get padding, because Windows draws them on a coloured plate and an
    icon touching the edge looks wrong. The small and target sizes are drawn close
    to full bleed, since they are already small enough that padding wastes them.

    Run this after changing Barline.ico. Output is committed, so a package build
    does not need it.
#>
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\..\src\Barline\Barline.ico",
    [string]$OutputDirectory = "$PSScriptRoot\assets"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# The 256px frame of the .ico is stored PNG-compressed, so it is lifted out
# verbatim rather than decoded and re-encoded through the Icon class, which does
# not always handle that frame.
function Get-SourceBitmap([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $count = [System.BitConverter]::ToUInt16($bytes, 4)

    $bestOffset = 0
    $bestLength = 0
    $bestSide = 0

    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + ($i * 16)
        $side = $bytes[$entry]
        if ($side -eq 0) { $side = 256 }

        if ($side -ge $bestSide) {
            $bestSide = $side
            $bestLength = [System.BitConverter]::ToUInt32($bytes, $entry + 8)
            $bestOffset = [System.BitConverter]::ToUInt32($bytes, $entry + 12)
        }
    }

    $frame = New-Object byte[] $bestLength
    [System.Array]::Copy($bytes, $bestOffset, $frame, 0, $bestLength)

    $stream = New-Object System.IO.MemoryStream(, $frame)
    return [System.Drawing.Image]::FromStream($stream)
}

function Write-Asset {
    param(
        [System.Drawing.Image]$Source,
        [string]$Path,
        [int]$Width,
        [int]$Height,
        [double]$Fill
    )

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # Square, and sized off the shorter edge so a wide tile keeps the icon square
    # and centred rather than stretching it.
    $side = [Math]::Round([Math]::Min($Width, $Height) * $Fill)
    $x = [Math]::Round(($Width - $side) / 2)
    $y = [Math]::Round(($Height - $side) / 2)

    $graphics.DrawImage($Source, $x, $y, $side, $side)
    $graphics.Dispose()

    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

if (-not (Test-Path $Source)) { throw "Icon not found: $Source" }

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

$icon = Get-SourceBitmap $Source
$written = 0

# Base sizes, and how much of each canvas the icon should cover. Tiles sit on a
# plate and need room; the small ones do not.
$tiles = @(
    @{ Name = 'Square71x71Logo';   W = 71;  H = 71;  Fill = 0.66 },
    @{ Name = 'Square150x150Logo'; W = 150; H = 150; Fill = 0.66 },
    @{ Name = 'Square310x310Logo'; W = 310; H = 310; Fill = 0.66 },
    @{ Name = 'Wide310x150Logo';   W = 310; H = 150; Fill = 0.66 },
    @{ Name = 'Square44x44Logo';   W = 44;  H = 44;  Fill = 1.00 },
    @{ Name = 'StoreLogo';         W = 50;  H = 50;  Fill = 1.00 }
)

foreach ($tile in $tiles) {
    foreach ($scale in 100, 125, 150, 200, 400) {
        $w = [Math]::Ceiling($tile.W * $scale / 100)
        $h = [Math]::Ceiling($tile.H * $scale / 100)
        $path = Join-Path $OutputDirectory "$($tile.Name).scale-$scale.png"

        Write-Asset -Source $icon -Path $path -Width $w -Height $h -Fill $tile.Fill
        $written++
    }
}

# Target sizes are what Windows reaches for in the taskbar, the Start list and
# Alt+Tab. The unplated variants are the same image drawn without the coloured
# backplate behind it, which is what a taskbar icon needs.
foreach ($size in 16, 24, 32, 48, 256) {
    foreach ($suffix in "targetsize-$size", "targetsize-$size.altform-unplated") {
        $path = Join-Path $OutputDirectory "Square44x44Logo.$suffix.png"

        Write-Asset -Source $icon -Path $path -Width $size -Height $size -Fill 1.00
        $written++
    }
}

$icon.Dispose()

Write-Host "Wrote $written assets to $OutputDirectory"
