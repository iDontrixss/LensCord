# Build OBS's DirectShow output filter adapted for Lenscord.
$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$obsRoot = Join-Path $projectRoot "third_party\obs-studio"
$payload = Join-Path $projectRoot "virtualcam\payload"
$dllName = "Lenscord.VirtualCamera.DShow.dll"

if (-not (Test-Path (Join-Path $obsRoot ".git"))) {
    throw "OBS fork is missing: $obsRoot"
}

Push-Location $obsRoot
try {
    & cmake --preset windows-ci-x64
    if ($LASTEXITCODE -ne 0) { throw "OBS CMake configuration failed (exit $LASTEXITCODE)." }

    & cmake --build build_x64 --config RelWithDebInfo --target obs-virtualcam-module
    if ($LASTEXITCODE -ne 0) { throw "OBS DirectShow build failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

$builtDll = Join-Path $obsRoot "build_x64\plugins\win-dshow\virtualcam-module\RelWithDebInfo\$dllName"
if (-not (Test-Path -LiteralPath $builtDll)) { throw "Build output not found: $builtDll" }

New-Item -ItemType Directory -Force -Path $payload | Out-Null
$payloadDll = Join-Path $payload $dllName
$backupDll = Join-Path $PSScriptRoot "$dllName.pre-obs.dll"
if ((Test-Path -LiteralPath $payloadDll) -and -not (Test-Path -LiteralPath $backupDll)) {
    Copy-Item -LiteralPath $payloadDll -Destination $backupDll
}
Copy-Item -LiteralPath $builtDll -Destination $payloadDll -Force
Write-Host "Lenscord OBS DirectShow module -> $payloadDll"
