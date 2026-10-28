<#
.SYNOPSIS
  Publishes the Windows build and compiles the Inno Setup installer.
.DESCRIPTION
  Produces dist\Clothesline-Setup-<version>-windows-x64.exe, a single self-contained setup
  that installs Clothesline without requiring a machine-wide .NET installation.
.PARAMETER Dotnet
  Path to dotnet.exe. Auto-detected when omitted.
.PARAMETER Iscc
  Path to ISCC.exe from Inno Setup. Auto-detected when omitted.
.PARAMETER OutputDir
  Where the finished setup is written. Defaults to a "dist" folder beside the repository.
.EXAMPLE
  .\scripts\build-windows-installer.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Dotnet = "",
    [string]$Iscc = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$iss = Join-Path $PSScriptRoot "windows-installer.iss"
$publish = Join-Path $root "_publish"
if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path -Parent $root) "dist" }

function Resolve-Tool([string]$given, [string[]]$candidates, [string]$what)
{
    if ($given)
    {
        if (-not (Test-Path $given)) { throw "$what not found at the given path: $given" }
        return (Resolve-Path $given).Path
    }
    foreach ($candidate in $candidates)
    {
        if ($candidate -and (Test-Path $candidate)) { return (Resolve-Path $candidate).Path }
    }
    $command = Get-Command (Split-Path -Leaf $candidates[0]) -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "Could not find $what. Install the .NET 10 SDK and Inno Setup 6 or 7, or pass -Dotnet / -Iscc explicitly."
}

$dotnet = Resolve-Tool $Dotnet @(
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet-sdk\dotnet.exe"),
    (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
) "dotnet.exe"

$iscc = Resolve-Tool $Iscc @(
    # A portable extraction beside the repository is the only option on a machine without
    # administrator rights, which is where this script is most likely to be run.
    (Join-Path $root "..\_tools\innosetup7\ISCC.exe"),
    (Join-Path $root "..\_tools\innosetup6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
) "ISCC.exe"

"dotnet : $dotnet"
"iscc   : $iscc"
"publish: $publish"
"output : $OutputDir"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish (Join-Path $root "windows\Clothesline.Windows\Clothesline.Windows.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publish "Clothesline.exe"
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }
"published: {0:N1} MB" -f ((Get-Item $exe).Length / 1MB)

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
& $iscc "/dMyPublishDir=$publish" "/dMyOutputDir=$OutputDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Get-ChildItem $OutputDir -Filter "Clothesline-Setup-*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1
""
"installer: $($setup.FullName)"
"size     : {0:N1} MB" -f ($setup.Length / 1MB)
