# Compila y ejecuta las pruebas del filtro DirectShow Lenscord.
#   run-tests.ps1              — factory + regsvr32 + body (grafo)
#   run-tests.ps1 -SoloFactory — sin registro ni grafo
#   run-tests.ps1 -Desinstalar — regsvr32 /u de la DLL
param([switch]$SoloFactory, [switch]$Desinstalar)

$ErrorActionPreference = "Stop"
$testsDir = $PSScriptRoot
$dll = "C:\Lenscord\virtualcam\payload\Lenscord.VirtualCamera.DShow.dll"
if (-not (Test-Path $dll)) { throw "No existe la DLL: $dll (ejecuta build-dshow.ps1 primero)." }

$bin = Join-Path $testsDir "bin"
$obj = Join-Path $bin "obj"
New-Item -ItemType Directory -Force -Path $bin, $obj | Out-Null

# --- compilar el exe de pruebas ------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "No hay Build Tools con VC.Tools.x86.x64." }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvarsall.bat"

$exe = Join-Path $bin "test-dshow.exe"
$cmd = "call `"$vcvars`" x64 && cl /nologo /EHsc /std:c++17 /W3" +
    " /Fo`"$obj\\`" /Fe`"$exe`"" +
    " `"$testsDir\test-dshow.cpp`"" +
    " /link ole32.lib oleaut32.lib strmiids.lib uuid.lib advapi32.lib shell32.lib user32.lib psapi.lib"
cmd /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de test-dshow.exe (exit $LASTEXITCODE)." }

# --- desinstalacion opcional ----------------------------------------------------
if ($Desinstalar) {
    $r = Start-Process -FilePath "regsvr32.exe" -ArgumentList "/u", "/s", $dll -Wait -PassThru
    Write-Host "regsvr32 /u -> exit $($r.ExitCode) (8x5 necesita administrador)"
    exit [int]$r.ExitCode
}

# --- prueba de factory (no requiere registro) ------------------------------------
Write-Host ""
Write-Host "----- PRUEBA: factory sin registro -----"
& $exe factory $dll
if ($LASTEXITCODE -ne 0) { throw "Prueba de factory fallo (exit $LASTEXITCODE)." }

if ($SoloFactory) { exit 0 }

# --- registro (regsvr32; requiere PowerShell como administrador) -----------------
Write-Host ""
Write-Host "----- regsvr32 (registro) -----"
$reg = Start-Process -FilePath "regsvr32.exe" -ArgumentList "/s", $dll -Wait -PassThru
Write-Host "regsvr32 -> exit $($reg.ExitCode)"
if ($reg.ExitCode -ne 0) {
    Write-Warning "regsvr32 fallo ($($reg.ExitCode)): ejecuta este script como ADMINISTRADOR."
    Write-Warning "(por ejemplo: Start-Process powershell -Verb RunAs)"
    exit [int]$reg.ExitCode
}

# --- cuerpo: enumeracion + grafo + frames -----------------------------------------
Write-Host ""
Write-Host "----- PRUEBA: registro + enumeracion + grafo -----"
& $exe body
if ($LASTEXITCODE -ne 0) { throw "Prueba de cuerpo fallo (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "TODAS LAS PRUEBAS PASARON"
