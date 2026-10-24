# Compila el filtro DirectShow "Lenscord" (webcam virtual para apps tipo Discord).
# Requiere Build Tools con sargas "VC.Tools.x86.x64" instaladas.
$ErrorActionPreference = "Stop"

$dir = $PSScriptRoot
$outDir = Join-Path $dir "bin\x64"
$objsDir = Join-Path $outDir "obj"
New-Item -ItemType Directory -Force -Path $outDir, $objsDir | Out-Null

# --- Locate Visual Build Tools ------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe no encontrado (Visual Studio Installer)." }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "No hay Build Tools con componentes VC.Tools.x86.x64." }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvarsall.bat"
if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat no existe en: $vsPath" }

# --- Compile ------------------------------------------------------------------
$dll = Join-Path $outDir "Lenscord.VirtualCamera.DShow.dll"

$cmd = "call `"$vcvars`" x64 && cl /nologo /std:c++17 /W3 /EHsc /O2 /GS /MT /DUNICODE /D_UNICODE /DNDEBUG" +
    " /Fo`"$objsDir\\`" /Fe`"$dll`"" +
    " `"$dir\LenscordDShow.cpp`"" +
    " /link /DLL /DEF:`"$dir\LenscordDShow.def`" /SUBSYSTEM:WINDOWS /IMPLIB:`"$objsDir\Lenscord.VirtualCamera.DShow.lib`"" +
    " kernel32.lib user32.lib ole32.lib oleaut32.lib advapi32.lib winmm.lib shell32.lib uuid.lib strmiids.lib dmoguids.lib"

cmd /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion (exit $LASTEXITCODE)." }

# --- Copy to the installer payload --------------------------------------------
$payload = Join-Path (Split-Path $dir -Parent) "payload"
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $payload "Lenscord.VirtualCamera.DShow.dll") -Force

Write-Host ""
Write-Host "OK -> $dll"
Write-Host "copiado a payload/ para install-vcam.ps1"
