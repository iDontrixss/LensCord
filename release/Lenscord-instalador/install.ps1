param(
    [switch]$AtajoEscritorio
)

$ErrorActionPreference = 'Stop'
$paquete = Split-Path -Parent $MyInvocation.MyCommand.Path
$data    = Join-Path $paquete 'data'
$build   = [Environment]::OSVersion.Version.Build
$esWin11 = $build -ge 22000

$archivosPrograma = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$raiz    = Join-Path $env:LOCALAPPDATA 'Lenscord'
$dirVcam = Join-Path $archivosPrograma 'Lenscord\VirtualCamera'
$dirData = Join-Path $env:ProgramData  'Lenscord'
$inicio  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lenscord.lnk'

$eventos = New-Object System.Collections.Generic.List[string]
function Log([string]$m) {
    Write-Host " - $m"
    $eventos.Add("[$(Get-Date -Format 'HH:mm:ss')] $m")
    try {
        New-Item -ItemType Directory -Path $raiz -Force | Out-Null
        Add-Content -LiteralPath (Join-Path $raiz 'log-instalador.txt') -Value $eventos[$eventos.Count - 1]
    } catch { }
}
function Iniciar-ServiciosCamara {
    foreach ($sn in @('FrameServer','FrameServerMonitor')) {
        try { Start-Service -Name $sn -ErrorAction Stop } catch { }
    }
}
function Pausa {
    Write-Host ''
    Read-Host 'Enter para cerrar la ventana' | Out-Null
}
function Fatal([string]$m) {
    Iniciar-ServiciosCamara
    Write-Host ''
    Write-Host ('  ERROR: ' + $m) -ForegroundColor Red
    Pausa
    exit 1
}
function Registrar-Dll([string]$dll, [switch]$Quitar) {
    $argumentos = if ($Quitar) { @('/s', '/u', "`"$dll`"") } else { @('/s', "`"$dll`"") }
    $p = Start-Process -FilePath 'regsvr32.exe' -ArgumentList $argumentos -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

$id  = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr  = [Security.Principal.WindowsPrincipal]::new($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $lanz = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
        if ($AtajoEscritorio) { $lanz += '-AtajoEscritorio' }
        Start-Process -FilePath 'powershell.exe' -ArgumentList $lanz -Verb RunAs
        exit 0
    } catch {
        Write-Host 'Hace falta permiso de administrador para registrar la camara virtual.' -ForegroundColor Red
        exit 1
    }
}

$Host.UI.RawUI.WindowTitle = 'Instalador de Lenscord'

Write-Host ''
Write-Host '  =============================='
Write-Host '   Instalador de Lenscord'
Write-Host '  =============================='
Write-Host "  Windows: $($build) $(if ($esWin11) {'(Win11)'} else {'(Win10: sin camara de sistema, pero Discord la ve igual)'})"
Write-Host ''

$exePaquete = Join-Path $data 'Lenscord.exe'
$dexPaquete = Join-Path $data 'lenscord.dex'
$vcamNombres = @('Lenscord.VirtualCamera.Source.dll','Lenscord.VirtualCamera.DShow.dll','Lenscord.VirtualCamera.Control.exe')
$vcamFuente = $vcamNombres | ForEach-Object { Join-Path $data 'vcam' | Join-Path -ChildPath $_ }
$adbNombres = @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll')
foreach ($f in @($exePaquete, $dexPaquete) + $vcamFuente) {
    if (-not (Test-Path -LiteralPath $f)) {
        Fatal "Falta un archivo del paquete: $f"
    }
}

Log ('1/4  Copiando la app a ' + $raiz)
try {
    New-Item -ItemType Directory -Path $raiz -Force | Out-Null
    Copy-Item -LiteralPath $exePaquete -Destination (Join-Path $raiz 'Lenscord.exe') -Force

    $mk = Join-Path $raiz 'dispositivo\out'
    New-Item -ItemType Directory -Path $mk -Force | Out-Null
    Copy-Item -LiteralPath $dexPaquete -Destination (Join-Path $mk 'lenscord.dex') -Force

    $pt = Join-Path $raiz 'tools\android-sdk\platform-tools'
    New-Item -ItemType Directory -Path $pt -Force | Out-Null
    foreach ($n in $adbNombres) {
        Copy-Item -LiteralPath (Join-Path $data ('adb\' + $n)) -Destination (Join-Path $pt $n) -Force
    }

    New-Item -ItemType Directory -Path (Join-Path $raiz 'evidencia') -Force | Out-Null

    @'
# Lenscord (instalacion compartida)

Carpeta creada por el instalador. La app deriva de aqui sus rutas:
tools\android-sdk\platform-tools (adb portable) y dispositivo\out\lenscord.dex.
La desinstalacion borra la carpeta entera.
'@ | Set-Content -LiteralPath (Join-Path $raiz 'PROGRESO.md') -Encoding UTF8

    Log ('    app lista: ' + $raiz)
} catch { Fatal ('No se pudo instalar la app: ' + $_.Exception.Message) }

Log '2/4  Verificando ffmpeg (decodificador de video)'
try {
    $sysFf = Get-Command 'ffmpeg.exe' -ErrorAction SilentlyContinue
    $propio = Join-Path $raiz 'tools\ffmpeg\ffmpeg.exe'
    if (Test-Path -LiteralPath $propio) {
        Log '    ffmpeg ya estaba instalado, se deja como esta'
    } elseif ($sysFf) {
        Log ('    se usara el ffmpeg del sistema: ' + $sysFf.Source)
    } else {
        Log '    no hay ffmpeg en el equipo; descargando una copia (~90 MB)...'
        $urlZip = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
        $tmpZip = Join-Path $env:TEMP 'lenscord-ffmpeg.zip'
        try {
            $hashEsperado = ((Invoke-WebRequest -Uri ($urlZip + '.sha256') -UseBasicParsing).Content).ToString().Trim().Split()[0]
            Invoke-WebRequest -Uri $urlZip -OutFile $tmpZip -UseBasicParsing
            $hashReal = (Get-FileHash -LiteralPath $tmpZip -Algorithm SHA256).Hash
            if ($hashReal -ne $hashEsperado) {
                Remove-Item -LiteralPath $tmpZip -Force -ErrorAction SilentlyContinue
                Fatal 'La descarga de ffmpeg no coincide con su suma SHA256. Vuelve a ejecutar el instalador.'
            }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($tmpZip)
            try {
                $ent = $zip.Entries | Where-Object { $_.Name -eq 'ffmpeg.exe' } | Select-Object -First 1
                if (-not $ent) { Fatal 'El paquete de ffmpeg descargado no contiene ffmpeg.exe.' }
                New-Item -ItemType Directory -Path (Split-Path -Parent $propio) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($ent, $propio, $true)
            } finally { $zip.Dispose() }
            Remove-Item -LiteralPath $tmpZip -Force -ErrorAction SilentlyContinue
        } catch {
            Fatal ('No se pudo descargar ffmpeg (' + $_.Exception.Message + '). Con internet vuelve a ejecutar el instalador.')
        }
        Log '    ffmpeg listo (copiado junto a la app)'
    }
} catch { Fatal ('ffmpeg: ' + $_.Exception.Message) }

Log '3/4  Instalando la camara virtual (se ve como webcam Lenscord en Discord, OBS, etc.)'
try {
    New-Item -ItemType Directory -Path $dirVcam -Force | Out-Null

    function Copiar-EnUso([string]$orig, [string]$dest) {
        for ($i = 1; $i -le 10; $i++) {
            try { Copy-Item -LiteralPath $orig -Destination $dest -Force -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 500 }
        }
        try {
            if (Test-Path -LiteralPath ($dest + '.old')) { Remove-Item -LiteralPath ($dest + '.old') -Force -ErrorAction SilentlyContinue }
            Move-Item -LiteralPath $dest -Destination ($dest + '.old') -Force
            Copy-Item -LiteralPath $orig -Destination $dest -Force
            Remove-Item -LiteralPath ($dest + '.old') -Force -ErrorAction SilentlyContinue
        } catch {
            Fatal ('No se pudo reemplazar ' + $dest + ' . Cierra Discord (y cualquier app que vea la camara) y vuelve a correr el instalador.')
        }
    }

    $dshowDest = Join-Path $dirVcam 'Lenscord.VirtualCamera.DShow.dll'
    if (Test-Path -LiteralPath $dshowDest) {
        $bloqueada = $false
        try { $s = [IO.File]::Open($dshowDest,'Open','ReadWrite','None'); $s.Dispose() } catch [IO.IOException] { $bloqueada = $true }
        if ($bloqueada) {
            try {
                $viejo = $dshowDest + '.old'
                if (Test-Path -LiteralPath $viejo) { Remove-Item -LiteralPath $viejo -Force -ErrorAction Stop }
                Move-Item -LiteralPath $dshowDest -Destination $viejo -Force -ErrorAction Stop
                Move-Item -LiteralPath $viejo -Destination $dshowDest -Force -ErrorAction Stop
            } catch {
                Fatal 'La DLL de la camara esta cargada por otra app (Discord?). Cierra Discord y cualquier app que la use, y vuelve a ejecutar el instalador.'
            }
        }
    }

    foreach ($sn in @('FrameServerMonitor','FrameServer')) {
        $svc = Get-Service -Name $sn -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') {
            Stop-Service -Name $sn -Force -ErrorAction SilentlyContinue
        }
    }
    Get-Process -Name 'WindowsCamera' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    foreach ($n in $vcamNombres) {
        Copiar-EnUso (Join-Path $data ('vcam\' + $n)) (Join-Path $dirVcam $n)
    }
    Log ('    archivos copiados a ' + $dirVcam)

    New-Item -ItemType Directory -Path $dirData -Force | Out-Null
    $escritores = @($id.User.Value)
    try {
        $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
        if ($explorer) {
            $sidUsuario = (Invoke-CimMethod -InputObject $explorer -MethodName GetOwnerSid).Sid
            if ($sidUsuario -and ($escritores -notcontains $sidUsuario)) { $escritores += $sidUsuario }
        }
    } catch { }
    $permisos = @('/inheritance:e')
    foreach ($sid in $escritores) { $permisos += @('/grant', ('*' + $sid + ':(OI)(CI)M')) }
    $permisos += @('/grant', '*S-1-5-32-545:(OI)(CI)RX', '/grant', '*S-1-5-19:(OI)(CI)R', '/grant', '*S-1-5-18:(OI)(CI)R')
    $null = & icacls.exe $dirData @permisos

    foreach ($n in @('Lenscord.VirtualCamera.Source.dll','Lenscord.VirtualCamera.DShow.dll')) {
        $codigo = Registrar-Dll (Join-Path $dirVcam $n)
        if ($codigo -ne 0) {
            Fatal "regsvr32 fallo para $n (codigo $codigo). Falta permiso de admin?"
        }
    }
    Log '    camara registrada en el sistema'

    if ($esWin11) {
        $null = (& (Join-Path $dirVcam 'Lenscord.VirtualCamera.Control.exe') 'create' 2>&1) | Out-String
        if ($LASTEXITCODE -eq 0) {
            Log '    camara de Windows 11 creada (MFCreateVirtualCamera, persistente)'
        } else {
            Log '    AVISO: no se pudo crear la camara de sistema; en apps DirectShow (Discord, OBS, Meet) la vas a encontrar igual.'
        }
    } else {
        Log '    Windows 10: la camara de sistema no existe; en apps DirectShow (Discord, OBS, Meet) la veras igual.'
    }
} catch { Fatal ('Camara virtual: ' + $_.Exception.Message) }

Log '4/4  Creando accesos'
try {
    $sh = New-Object -ComObject WScript.Shell
    $sc = $sh.CreateShortcut($inicio)
    $sc.TargetPath       = Join-Path $raiz 'Lenscord.exe'
    $sc.WorkingDirectory = $raiz
    $sc.IconLocation     = (Join-Path $raiz 'Lenscord.exe') + ',0'
    $sc.Description      = 'Lenscord: tu telefono como webcam'
    $sc.Save()
    if ($AtajoEscritorio) {
        $desk = [Environment]::GetFolderPath('Desktop')
        $sc2 = $sh.CreateShortcut((Join-Path $desk 'Lenscord.lnk'))
        $sc2.TargetPath       = $sc.TargetPath
        $sc2.WorkingDirectory = $raiz
        $sc2.IconLocation     = $sc.IconLocation
        $sc2.Save()
    }
    Log '    accesos listos'
} catch { Log ('    AVISO accesos: ' + $_.Exception.Message) }

Iniciar-ServiciosCamara

$colorAnterior = [Console]::ForegroundColor; [Console]::ForegroundColor = 'Green'
Write-Host ''
Write-Host '  ==========================================================='
Write-Host '   LISTO. Lenscord instalado, camara virtual ya activa.'
Write-Host '  ==========================================================='
[Console]::ForegroundColor = $colorAnterior
Write-Host ''
Write-Host '  Como usarlo:'
Write-Host '   1. Arranca Lenscord desde el menu de inicio.'
Write-Host '   2. Conecta el celu por USB con Depuracion USB activada.'
Write-Host '      (Ajustes > Opciones de desarrollador > Depuracion USB)'
Write-Host '      Presiona "Iniciar camara". Wi-Fi tambien anda, panel arriba a la derecha.'
Write-Host '   3. Discord: Configuracion > Video > Camara  ->  "Lenscord".'
Write-Host '   4. Para parar: boton "Detener". No queda nada instalado en el celu.'
Write-Host ''
Write-Host '  Para desinstalar: Desinstalar-Lenscord.cmd en esta misma carpeta.'
Write-Host ''
Pausa
