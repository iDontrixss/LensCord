$ErrorActionPreference = "Stop"
$targetDirectory = Join-Path $env:ProgramFiles "Lenscord\VirtualCamera"
$sourceDll = Join-Path $targetDirectory "Lenscord.VirtualCamera.Source.dll"
$dshowDll = Join-Path $targetDirectory "Lenscord.VirtualCamera.DShow.dll"
$controlExe = Join-Path $targetDirectory "Lenscord.VirtualCamera.Control.exe"
$dataDirectory = Join-Path $env:ProgramData "Lenscord"
$logPath = Join-Path $dataDirectory "virtual-camera-install.log"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Administrator privileges are required to remove the virtual camera."
}

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
Add-Content -LiteralPath $logPath -Value "[$(Get-Date -Format o)] Starting Lenscord virtual camera removal."
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
    if (Test-Path -LiteralPath $controlExe) {
        $removeExitCode = Invoke-CameraControl $controlExe "remove"
        if ($removeExitCode -ne 0) {
            throw "Failed to remove the Windows virtual camera (control exit $removeExitCode)."
        }
        Write-InstallLog "Persistent virtual camera removed."
    }

    Stop-CameraServices

    if (Test-Path -LiteralPath $sourceDll) {
        & regsvr32.exe /s /u $sourceDll
    }

    if (Test-Path -LiteralPath $dshowDll) {
        & regsvr32.exe /s /u $dshowDll
        Write-InstallLog "DirectShow virtual camera unregistered."
    }

    if (Test-Path -LiteralPath $targetDirectory) {
        $resolvedProgramFiles = [System.IO.Path]::GetFullPath($env:ProgramFiles)
        $resolvedTarget = [System.IO.Path]::GetFullPath($targetDirectory)
        if (-not $resolvedTarget.StartsWith($resolvedProgramFiles, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a target outside Program Files."
        }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
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

Write-Host "Lenscord Windows Virtual Camera removed."
