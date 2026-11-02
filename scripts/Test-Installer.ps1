param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$output = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/installer-test'
New-Item -ItemType Directory -Path $output | Out-Null
$target = Join-Path $output 'Custom Install Path/Zipora'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Alnitak44.Zipora_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'Existing installation found; run this test only on a disposable runner.' }
$probe = Join-Path $env:APPDATA 'Zipora/installer-preservation-probe.txt'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $probe) | Out-Null
$value = [Guid]::NewGuid().ToString()
$value | Set-Content -LiteralPath $probe

function Run-Installer([string[]]$Extra, [string]$LogName) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/TASKS=contextmenu',('/LOG="'+(Join-Path $output $LogName)+'"')) + $Extra
    $process = Start-Process -FilePath $Installer -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode). See $LogName" }
}
Run-Installer @('/DIR="'+$target+'"') 'first-install.log'
$installed = (Get-ItemProperty -LiteralPath $uninstallKey).InstallLocation.TrimEnd('\')
if ($installed -ne $target.TrimEnd('\')) { throw 'Incorrect installation directory.' }
$exe = Join-Path $target 'Zipora.exe'
$expected = (Get-FileHash -LiteralPath $exe).Hash
$nativeKey = 'HKLM:\SOFTWARE\Classes\CLSID\{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}\InprocServer32'
if ((Get-Item -LiteralPath $nativeKey).GetValue('') -ne (Join-Path $target 'Zipora.ShellExtension.dll')) { throw 'Context menu registration mismatch.' }
$dictionary = Join-Path $target 'dictionaries/user-added.txt'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dictionary) | Out-Null
$value | Set-Content -LiteralPath $dictionary
'stale executable test fixture' | Set-Content -LiteralPath $exe

# No /DIR on the second invocation: the installer MUST find the existing path.
Run-Installer @() 'upgrade.log'
if ((Get-FileHash -LiteralPath $exe).Hash -ne $expected) { throw 'Upgrade did not replace the old program.' }
if ((Get-Content -LiteralPath $dictionary -Raw).Trim() -ne $value) { throw 'Upgrade changed a user dictionary.' }
if ((Get-Content -LiteralPath $probe -Raw).Trim() -ne $value) { throw 'Upgrade changed AppData.' }

$process = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $output 'uninstall.log')+'"')) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw 'Uninstall failed.' }
if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $nativeKey)) { throw 'Uninstall left program/owned registration behind.' }
if ((Get-Content -LiteralPath $probe -Raw).Trim() -ne $value -or (Get-Content -LiteralPath $dictionary -Raw).Trim() -ne $value) { throw 'Uninstall deleted user data.' }
Write-Output 'PASS: install, custom path discovery, overwrite upgrade, context menu registration/unregistration, AppData and dictionary preservation.'
