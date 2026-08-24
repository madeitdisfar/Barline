<#
.SYNOPSIS
    Builds the MSIX package for the Store.

.DESCRIPTION
    Publishes the app, lays the payload out beside the manifest and assets,
    compiles the resource index, and packs the result.

    Deliberately script-driven rather than a Visual Studio packaging project. A
    .wapproj cannot be built by "dotnet build", which would tie releases and CI to
    a Visual Studio install, and it keeps the manifest as generated output rather
    than a file that can be reviewed in a diff.

.PARAMETER Register
    Skips packing and registers the layout folder directly. This is the fast path
    for testing: it installs as a genuine packaged app, so the startup task and
    the redirected data paths behave exactly as they will from the Store, with no
    certificate involved. Requires Developer Mode.

.EXAMPLE
    .\Build-Package.ps1 -Register
    Build and install locally for testing.

.EXAMPLE
    .\Build-Package.ps1
    Produce artifacts\Barline.msix for upload to Partner Center.
#>
[CmdletBinding()]
param(
    [switch]$Register,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path "$PSScriptRoot\.."
$project = "$root\src\Barline\Barline.csproj"
$layout = "$root\artifacts\package"
$output = "$root\artifacts\Barline.msix"

# ---- Locate the SDK tools ------------------------------------------------

# Highest installed SDK that actually carries the packaging tools. Picking the
# newest by name would happily select a version that predates makepri.
$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { Test-Path (Join-Path $_ 'makeappx.exe') } |
    Select-Object -First 1

if (-not $sdkBin) { throw 'Windows SDK packaging tools not found. Install the Windows SDK.' }

$makeappx = Join-Path $sdkBin 'makeappx.exe'
$makepri = Join-Path $sdkBin 'makepri.exe'

Write-Host "SDK tools: $sdkBin"

# ---- Read the version from the project -----------------------------------

[xml]$csproj = Get-Content $project

# By XPath rather than by property: the project has several PropertyGroups and
# only one carries Version, so walking them as objects trips over the ones that
# do not have it.
$versionNode = $csproj.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw "No <Version> in $project." }
$projectVersion = $versionNode.InnerText.Trim()

# Four parts, and the last is always 0 because the Store reserves the revision
# component. Stamped into the layout's manifest below rather than kept in step by
# hand, so bumping the project version is the only edit a release needs.
$packageVersion = "$projectVersion.0"

[xml]$manifest = Get-Content "$PSScriptRoot\AppxManifest.xml"

if ($manifest.Package.Identity.Publisher -like '*REPLACE*') {
    Write-Warning 'The manifest still has placeholder identity values from Partner Center.'
    Write-Warning 'The package will build and register locally, but Partner Center will reject it.'
}

Write-Host "Version: $packageVersion (from $([System.IO.Path]::GetFileName($project)))"

# ---- Publish -------------------------------------------------------------

# A previous -Register run leaves the app installed from this very folder, so a
# copy left running holds its own executable open and the rebuild fails on a
# locked file. Only processes running from the layout are stopped; an instance
# the user is running from anywhere else is left alone.
Get-Process Barline -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($layout, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host "Stopping the copy running from the layout (pid $($_.Id))"
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit(5000) | Out-Null
    }

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null

Write-Host 'Publishing...'

# Loose assemblies rather than the single file the portable build ships as. The
# project turns PublishSingleFile on for Release because one downloaded file is the
# whole point of a portable zip; a package is a folder either way, so bundling buys
# nothing here and costs memory. Measured on the same Release build: single-file sat
# at 117.6 MB of private working set against 91.1 MB loose, because assemblies in a
# bundle are private to the process while loose ones are mapped from disk and shared.
dotnet publish $project -c $Configuration -o $layout --nologo -v quiet -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# ---- Lay out the package -------------------------------------------------

Copy-Item "$PSScriptRoot\assets" $layout -Recurse

# The version is stamped here rather than edited in the source manifest, so the
# project file stays the one place a release version is written.
$manifest.Package.Identity.Version = $packageVersion
$manifest.Save("$layout\AppxManifest.xml")

# The manifest names assets without their scale suffix, and the resource index is
# what maps that to the right file for the display in front of the user. Without
# it every logo silently falls back to nothing.
Write-Host 'Building resource index...'
$priConfig = Join-Path $env:TEMP 'barline-priconfig.xml'

& $makepri createconfig /ConfigXml $priConfig /Default en-US /Overwrite | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed.' }

& $makepri new /ProjectRoot $layout /ConfigXml $priConfig /OutputFile "$layout\resources.pri" /Overwrite | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri new failed.' }

Remove-Item $priConfig -Force -ErrorAction SilentlyContinue

# ---- Register, or pack ---------------------------------------------------

if ($Register) {
    # A development registration cannot replace itself at the same version, and
    # the version only changes on a release, so every rebuild in a working
    # session would otherwise fail. Removing first makes -Register repeatable.
    $installed = Get-AppxPackage -Name $manifest.Package.Identity.Name -ErrorAction SilentlyContinue
    if ($installed) {
        Write-Host "Removing the previous registration ($($installed.PackageFullName))"
        Remove-AppxPackage -Package $installed.PackageFullName
    }

    Write-Host 'Registering the layout...'
    Add-AppxPackage -Register "$layout\AppxManifest.xml"
    Write-Host 'Installed. Launch it from Start, or run Barline.exe from the layout.'
    return
}

Write-Host 'Packing...'
if (Test-Path $output) { Remove-Item $output -Force }

& $makeappx pack /d $layout /p $output /overwrite | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }

$size = [Math]::Round((Get-Item $output).Length / 1MB, 1)
Write-Host "Packed $output ($size MB)"
Write-Host 'Unsigned, which is what Partner Center expects. It signs on submission.'
