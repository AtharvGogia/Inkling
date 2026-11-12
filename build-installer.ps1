# Builds Inkling.exe (single file, self-contained) and the per-user installer dist\Inkling-Setup-<version>.exe.
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

[xml]$proj = Get-Content (Join-Path $root 'Inkling\Inkling.csproj')
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Inkling.csproj' }

Write-Host "Publishing Inkling $version..."
dotnet publish (Join-Path $root 'Inkling') -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

Write-Host "Building installer with $iscc..."
& $iscc /Qp "/DAppVersion=$version" (Join-Path $root 'installer\Inkling.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$out = Join-Path $root "dist\Inkling-Setup-$version.exe"
Write-Host "Done: $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
