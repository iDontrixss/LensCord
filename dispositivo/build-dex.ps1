# Compila el payload del telefono a un .dex (sin Gradle, sin Android Studio)
# Uso: powershell -ExecutionPolicy Bypass -File build-dex.ps1
$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path      # ...\Lenscord\dispositivo
$proj    = Split-Path -Parent $root                            # ...\Lenscord
$sdk     = Join-Path $proj "tools\android-sdk"
$androidJar = Join-Path $sdk "platforms\android-35\android.jar"
$d8bat   = Join-Path $sdk "build-tools\34.0.0\d8.bat"
$src     = Join-Path $root "src"
$build   = Join-Path $root "build"
$outDex  = Join-Path $root "out\lenscord.dex"

# d8 de build-tools 34 pide Java 11+; aca se busca el JDK mas nuevo disponible.
function Find-Jdk {
    $cands = @()
    foreach ($base in @($env:JAVA_HOME, "C:\Program Files\Microsoft", "C:\Program Files\Eclipse Adoptium",
                        "C:\Program Files\Java", "C:\Program Files\Zulu", "C:\Program Files\BellSoft")) {
        if (-not $base -or -not (Test-Path $base)) { continue }
        if (Test-Path (Join-Path $base "bin\java.exe")) { $cands += $base; continue }
        Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "bin\java.exe") } |
            ForEach-Object { $cands += $_.FullName }
    }
    $cands | Sort-Object -Descending | Select-Object -First 1
}

$jdk = Find-Jdk
if (-not $jdk) { throw "No se encontro un JDK. d8 requiere Java 11 o superior." }
$env:JAVA_HOME = $jdk
Write-Host "JDK: $jdk" -ForegroundColor Cyan

foreach ($f in @($androidJar, $d8bat)) {
    if (-not (Test-Path $f)) { throw "Falta $f (Fase 0: descarga el SDK a tools\android-sdk)" }
}

if (Test-Path $build) { Remove-Item $build -Recurse -Force }
New-Item -ItemType Directory -Path $build -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $outDex) -Force | Out-Null

$javac = Join-Path $jdk "bin\javac.exe"

# En PowerShell 5.1, ErrorActionPreference=Stop convierte cualquier escritura en
# stderr de un proceso nativo en error terminante. Para javac/d8 eso es normal
# (warnings y "deprecado"), asi que se baja solo alrededor de las llamadas.
$ErrorActionPreference = "Continue"

Write-Host "javac -> .class" -ForegroundColor Cyan
& $javac -source 8 -target 8 -nowarn `
    -bootclasspath $androidJar `
    -classpath $androidJar `
    -d $build `
    (Join-Path $src "com\lenscord\Acquire.java") `
    (Join-Path $src "com\lenscord\LenscordDevice.java")
$rc = $LASTEXITCODE
if ($rc -ne 0) { $ErrorActionPreference = "Stop"; throw "javac fallo (codigo $rc)" }

Write-Host "d8 -> .dex" -ForegroundColor Cyan
# d8 (build-tools 34) NO acepta un archivo .dex como --output: exige .zip/.jar o un
# directorio existente. Se genera el directorio y se toma classes.dex.
$dexDir = Join-Path $root "out\dexdir"
if (Test-Path $dexDir) { Remove-Item $dexDir -Recurse -Force }
New-Item -ItemType Directory -Path $dexDir -Force | Out-Null

$classes = @(Get-ChildItem $build -Recurse -Filter *.class | Select-Object -ExpandProperty FullName)
if ($classes.Count -eq 0) { $ErrorActionPreference = "Stop"; throw "javac no produjo ningun .class" }

& $d8bat --release --min-api 26 --lib $androidJar --output $dexDir $classes
$rc = $LASTEXITCODE
if ($rc -ne 0) { $ErrorActionPreference = "Stop"; throw "d8 fallo (codigo $rc)" }

$ErrorActionPreference = "Stop"
if (Test-Path $outDex) { Remove-Item $outDex -Force }
Move-Item (Join-Path $dexDir "classes.dex") $outDex
Remove-Item $dexDir -Recurse -Force
$len = (Get-Item $outDex).Length
Write-Host "OK  $outDex  ($([math]::Round($len/1KB,1)) KB, $($classes.Count) clases)" -ForegroundColor Green