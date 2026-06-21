#requires -version 5
<#
.SYNOPSIS
  Build (and optionally publish/run) VirtualMirage. Locates a .NET 8 SDK automatically.
.EXAMPLE
  .\build.ps1            # Release build
  .\build.ps1 -Publish   # single-folder publish to .\publish
  .\build.ps1 -Run       # build then launch the tray app
#>
param(
    [switch]$Publish,
    [switch]$Run,
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$proj = Join-Path $root 'src\VirtualMirage\VirtualMirage.csproj'

function Get-Dotnet {
    $candidates = @(
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe"
    )
    $cmd = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if ($cmd) { $candidates += $cmd.Source }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks) { return $c }
        }
    }
    throw "No .NET 8 SDK found. Install it with 'winget install Microsoft.DotNet.SDK.8' or the dotnet-install.ps1 script (see README)."
}

$dotnet = Get-Dotnet
Write-Host "Using SDK: $dotnet"

if ($Publish) {
    $out = Join-Path $root 'publish'
    & $dotnet publish $proj -c $Configuration -o $out
    Write-Host "Published to $out"
}
else {
    & $dotnet build $proj -c $Configuration
}

if ($Run) {
    $exe = Join-Path $root "src\VirtualMirage\bin\$Configuration\net8.0-windows\VirtualMirage.exe"
    if (Test-Path $exe) { Start-Process $exe; Write-Host "Launched $exe" }
    else { Write-Warning "Executable not found at $exe" }
}
