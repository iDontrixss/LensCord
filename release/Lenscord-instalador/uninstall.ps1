$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'Desinstalador de Lenscord'

$archivosPrograma = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$raiz    = Join-Path $env:LOCALAPPDATA 'Lenscord'
$dirVcam = Join-Path $archivosPrograma 'Lenscord\VirtualCamera'
$dirData = Join-Path $env:ProgramData  'Lenscord'
$inicio  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lenscord.lnk'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = [Security.Principal.WindowsPrincipal]::new($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        Start-Process -FilePath 'powershell.exe' `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"") `
            -Verb RunAs
        exit 0
    } catch {
        Write-Host 'Hace falta permiso de administrador.' -ForegroundColor Red
        exit 1
    }
}

$avisos = New-Object System.Collections.Generic.List[string]
function Paso([string]$descripcion, [scriptblock]$accion) {
    try {
        & $accion
        Write-Host " - $descripcion"
    } catch {
        $avisos.Add("$descripcion : $($_.Exception.Message)")
        Write-Host " ! $descripcion (fallo: $($_.Exception.Message))" -ForegroundColor Yellow
    }
}

Paso 'app cerrada' {
    Get-Process -Name 'Lenscord' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

$ctl = Join-Path $dirVcam 'Lenscord.VirtualCamera.Control.exe'
if (Test-Path -LiteralPath $ctl) {
    Paso 'camara de Windows 11 eliminada' {
        $null = (& $ctl 'remove' 2>&1) | Out-String
        if ($LASTEXITCODE -ne 0) { throw "codigo $LASTEXITCODE" }
    }
}

Paso 'servicios de camara detenidos' {
    foreach ($sn in @('FrameServerMonitor','FrameServer')) {
        $svc = Get-Service -Name $sn -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $sn -Force }
    }
    Get-Process -Name 'WindowsCamera' -ErrorAction SilentlyContinue | Stop-Process -Force
}

foreach ($n in @('Lenscord.VirtualCamera.Source.dll','Lenscord.VirtualCamera.DShow.dll')) {
    $dll = Join-Path $dirVcam $n
    if (Test-Path -LiteralPath $dll) {
        Paso "registro COM de $n eliminado" {
            $p = Start-Process -FilePath 'regsvr32.exe' -ArgumentList @('/s', '/u', "`"$dll`"") -Wait -PassThru -WindowStyle Hidden
            if ($p.ExitCode -ne 0) { throw "regsvr32 codigo $($p.ExitCode)" }
        }
    }
}

$pfCompleto = [IO.Path]::GetFullPath($archivosPrograma)
if ((Test-Path -LiteralPath $dirVcam) -and $dirVcam.StartsWith($pfCompleto + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Paso 'DLLs y Control.exe borrados' {
        Get-ChildItem -LiteralPath $dirVcam -Force -Recurse -File | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop }
            catch { Move-Item -LiteralPath $_.FullName -Destination ($_.FullName + '.old') -Force -ErrorAction Stop }
        }
        $restantes = Get-ChildItem -LiteralPath $dirVcam -Force -Recurse -File | Where-Object { $_.Extension -ne '.old' }
        if ($restantes) { throw 'quedan archivos en uso; cierra Discord u otras apps de video y repite' }
        Remove-Item -LiteralPath $dirVcam -Recurse -Force -ErrorAction SilentlyContinue
    }
}

foreach ($sn in @('FrameServer','FrameServerMonitor')) {
    try { Start-Service -Name $sn -ErrorAction Stop } catch { }
}

Paso 'accesos borrados' {
    Remove-Item -LiteralPath $inicio -Force -ErrorAction SilentlyContinue
    $desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lenscord.lnk'
    Remove-Item -LiteralPath $desk -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $raiz) {
    Paso 'carpeta de la app borrada' { Remove-Item -LiteralPath $raiz -Recurse -Force }
}
if (Test-Path -LiteralPath $dirData) {
    Paso 'frames compartidos borrados' { Remove-Item -LiteralPath $dirData -Recurse -Force }
}

Write-Host ''
if ($avisos.Count -eq 0) {
    Write-Host 'Lenscord desinstalado.' -ForegroundColor Green
} else {
    Write-Host 'Lenscord se desinstalo con avisos:' -ForegroundColor Yellow
    foreach ($a in $avisos) { Write-Host "  - $a" -ForegroundColor Yellow }
}
Read-Host 'Enter para cerrar' | Out-Null
