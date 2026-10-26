param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory
)

$ErrorActionPreference = "Stop"
$sourceUnregistered = $false
$programFilesRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$targetDirectory = Join-Path $programFilesRoot "Lenscord\VirtualCamera"
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

function Set-DataDirectoryAcl {
    $writers = @($identity.User.Value)
    try {
        $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
        if ($explorer) {
            $ownerSid = (Invoke-CimMethod -InputObject $explorer -MethodName GetOwnerSid).Sid
            if ($ownerSid -and ($writers -notcontains $ownerSid)) { $writers += $ownerSid }
        }
    }
    catch { }
    $grants = @('/inheritance:e')
    foreach ($sid in $writers) { $grants += @('/grant', ('*' + $sid + ':(OI)(CI)M')) }
    $grants += @('/grant', '*S-1-5-32-545:(OI)(CI)RX', '/grant', '*S-1-5-19:(OI)(CI)R', '/grant', '*S-1-5-18:(OI)(CI)R')
    & icacls.exe $dataDirectory @grants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set permissions on the shared-frame directory (icacls exit $LASTEXITCODE)." }
}
function Invoke-Regsvr32([string]$Dll, [switch]$Unregister) {
    $arguments = if ($Unregister) { @('/s', '/u', "`"$Dll`"") } else { @('/s', "`"$Dll`"") }
    $process = Start-Process -FilePath 'regsvr32.exe' -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
Set-DataDirectoryAcl
if (Test-Path -LiteralPath $logPath) {
    if ((Get-Item -LiteralPath $logPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Remove-Item -LiteralPath $logPath -Force
    }
}
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
        $unregisterCode = Invoke-Regsvr32 $oldSource -Unregister
        $sourceUnregistered = $true
        Write-InstallLog "Old COM source unregister returned exit code $unregisterCode."
    }

    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    foreach ($payloadFile in $payloadFiles) {
        Write-InstallLog "Copying $($payloadFile.Name)."
        Copy-PayloadFile $payloadFile.FullName (Join-Path $targetDirectory $payloadFile.Name)
    }

    $registerCode = Invoke-Regsvr32 $installedSource
    if ($registerCode -ne 0) { throw "Failed to register the virtual camera COM source (regsvr32 exit $registerCode)." }
    $sourceUnregistered = $false
    Write-InstallLog "COM source registered."

    $registerCode = Invoke-Regsvr32 $installedDShow
    if ($registerCode -ne 0) { throw "Failed to register the DirectShow virtual camera (regsvr32 exit $registerCode)." }
    Write-InstallLog "DirectShow virtual camera registered."

    $createExitCode = Invoke-CameraControl (Join-Path $targetDirectory $controlExeName) "create"
    if ($createExitCode -ne 0) {
        $null = Invoke-Regsvr32 $installedSource -Unregister
        $null = Invoke-Regsvr32 $installedDShow -Unregister
        throw "Failed to create the Windows virtual camera (control exit $createExitCode)."
    }

    Write-InstallLog "Lenscord Windows Virtual Camera installed successfully."
    Write-Host "Lenscord Windows Virtual Camera installed."
}
catch {
    Write-InstallLog "ERROR: $($_.Exception.Message)"
    if ($sourceUnregistered -and (Test-Path -LiteralPath $installedSource)) {
        $null = Invoke-Regsvr32 $installedSource
        Write-InstallLog "Previous COM source registration restored."
    }
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
