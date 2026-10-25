param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory
)

$ErrorActionPreference = "Stop"
$targetDirectory = Join-Path $env:ProgramFiles "Lenscord\VirtualCamera"
$dataDirectory = Join-Path $env:ProgramData "Lenscord"
$sourceDllName = "Lenscord.VirtualCamera.Source.dll"
$dshowDllName = "Lenscord.VirtualCamera.DShow.dll"
$controlExeName = "Lenscord.VirtualCamera.Control.exe"
$logPath = Join-Path $dataDirectory "virtual-camera-install.log"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Administrator privileges are required to install the virtual camera."
}

if ([Environment]::OSVersion.Version.Build -lt 22000) {
    throw "The Lenscord virtual camera requires Windows 11 build 22000 or newer."
}

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
Set-Content -LiteralPath $logPath -Value "[$(Get-Date -Format o)] Starting Lenscord virtual camera installation."
function Write-InstallLog([string]$Message) {
    Add-Content -LiteralPath $logPath -Value "[$(Get-Date -Format o)] $Message"
}
function Invoke-CameraControl([string]$Executable, [string]$Command) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $Executable $Command 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $output | ForEach-Object { Write-InstallLog $_.ToString() }
    return $exitCode
}
function Copy-PayloadFile([string]$Source, [string]$Destination) {
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 20) { throw }
            Write-InstallLog "Copy attempt $attempt failed for $(Split-Path -Leaf $Source); waiting for camera processes to release it."
            Start-Sleep -Milliseconds 500
        }
    }
}
function Assert-FileReplaceable([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        throw "Close Discord and any other app using Lenscord Virtual Camera, then retry. The camera DLL is still loaded: $Path"
    }
    finally {
        if ($stream) { $stream.Dispose() }
    }
}

$resolvedPayload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$sourceDll = Join-Path $resolvedPayload $sourceDllName
$dshowDll = Join-Path $resolvedPayload $dshowDllName
$controlExe = Join-Path $resolvedPayload $controlExeName
if (-not (Test-Path -LiteralPath $sourceDll)) { throw "Virtual camera source not found: $sourceDll" }
if (-not (Test-Path -LiteralPath $dshowDll)) { throw "DirectShow virtual camera not found: $dshowDll" }
if (-not (Test-Path -LiteralPath $controlExe)) { throw "Virtual camera control app not found: $controlExe" }
$payloadFiles = @(Get-ChildItem -LiteralPath $resolvedPayload -File |
    Where-Object { $_.Name -in @($sourceDllName, $dshowDllName, $controlExeName) })

$cameraServiceNames = @("FrameServerMonitor", "FrameServer")
$cameraServicesToRestart = @($cameraServiceNames | Where-Object {
    $service = Get-Service -Name $_ -ErrorAction SilentlyContinue
    $service -and $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
})
function Stop-CameraServices {
    Get-Process -Name "WindowsCamera" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-InstallLog "Closing Windows Camera process $($_.Id) for virtual-camera maintenance."
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($serviceName in $cameraServiceNames) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-InstallLog "Stopping service $serviceName."
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            (Get-Service -Name $serviceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(15))
        }
    }
}
function Restart-CameraServices {
    foreach ($serviceName in @("FrameServer", "FrameServerMonitor")) {
        if ($cameraServicesToRestart -contains $serviceName) {
            Start-Service -Name $serviceName -ErrorAction Stop
            Write-InstallLog "Service $serviceName restarted."
        }
    }
}

try {
    Stop-CameraServices

    $installedSource = Join-Path $targetDirectory $sourceDllName
    $installedDShow = Join-Path $targetDirectory $dshowDllName
    Assert-FileReplaceable $installedDShow

    $oldSource = Join-Path $targetDirectory $sourceDllName
    Write-InstallLog "Keeping the persistent virtual-camera registration while replacing its COM source."
    if (Test-Path -LiteralPath $oldSource) {
        & regsvr32.exe /s /u $oldSource
        Write-InstallLog "Old COM source unregister returned exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    foreach ($payloadFile in $payloadFiles) {
        Write-InstallLog "Copying $($payloadFile.Name)."
        Copy-PayloadFile $payloadFile.FullName (Join-Path $targetDirectory $payloadFile.Name)
    }

    & icacls.exe $dataDirectory /inheritance:e /grant '*S-1-5-32-545:(OI)(CI)M' '*S-1-5-19:(OI)(CI)R' '*S-1-5-18:(OI)(CI)R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set permissions on the shared-frame directory (icacls exit $LASTEXITCODE)." }
    Write-InstallLog "Shared-frame permissions updated."

    & regsvr32.exe /s $installedSource
    if ($LASTEXITCODE -ne 0) { throw "Failed to register the virtual camera COM source (regsvr32 exit $LASTEXITCODE)." }
    Write-InstallLog "COM source registered."

    # Filtro DirectShow: es el que Discover y libwebrtc (Discord, etc.) ven.
    & regsvr32.exe /s $installedDShow
    if ($LASTEXITCODE -ne 0) { throw "Failed to register the DirectShow virtual camera (regsvr32 exit $LASTEXITCODE)." }
    Write-InstallLog "DirectShow virtual camera registered."

    $createExitCode = Invoke-CameraControl (Join-Path $targetDirectory $controlExeName) "create"
    if ($createExitCode -ne 0) {
        & regsvr32.exe /s /u $installedSource
        & regsvr32.exe /s /u $installedDShow
        throw "Failed to create the Windows virtual camera (control exit $createExitCode)."
    }

    Write-InstallLog "Lenscord Windows Virtual Camera installed successfully."
    Write-Host "Lenscord Windows Virtual Camera installed."
}
catch {
    Write-InstallLog "ERROR: $($_.Exception.Message)"
    throw
}
finally {
    try {
        Restart-CameraServices
    }
    catch {
        Write-InstallLog "WARNING: Failed to restart camera services: $($_.Exception.Message)"
    }
}
