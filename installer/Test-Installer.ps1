# WinVM installer test: silent per-user install (with desktop shortcut) -> verify -> launch -> close -> silent uninstall -> verify clean.
# Usage: powershell -ExecutionPolicy Bypass -File Test-Installer.ps1 -Setup <WinVM-1.5.0-Setup.exe>
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') { if ($ok) { Write-Host "PASS $name $detail" } else { Write-Host "FAIL $name $detail"; $script:fail++ } }
$t = Join-Path $env:TEMP 'winvm-inst-test'; New-Item -ItemType Directory -Force $t | Out-Null
$app = Join-Path $env:LOCALAPPDATA 'Programs\WinVM'
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinVM.lnk'
$desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinVM.lnk'
$reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
if (Test-Path (Join-Path $app 'unins000.exe')) { throw "WinVM is already installed in $app; uninstall it first." }
$p = Start-Process $Setup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/CURRENTUSER','/NORESTART','/TASKS=desktopicon',"/LOG=$t\install.log" -Wait -PassThru
Check 'install.exit' ($p.ExitCode -eq 0) "code=$($p.ExitCode)"
$exe = Join-Path $app 'WinVM.exe'
Check 'install.exe' ((Test-Path $exe) -and (Get-Item $exe).VersionInfo.ProductVersion -like '1.5.0*') $exe
Check 'install.startmenu' (Test-Path $lnk) $lnk
Check 'install.desktop' (Test-Path $desk) $desk
$u = @(Get-ItemProperty $reg -EA SilentlyContinue | Where-Object DisplayName -like 'WinVM*')
Check 'install.uninstall-entry' ($u.Count -eq 1 -and $u[0].DisplayVersion -eq '1.5.0') "$($u.DisplayName)"
$wp = Start-Process $exe -PassThru; Start-Sleep 8; $wp.Refresh()
Check 'launch' (-not $wp.HasExited) "pid=$($wp.Id)"
if (-not $wp.HasExited) { [void]$wp.CloseMainWindow(); if (-not $wp.WaitForExit(5000)) { $wp.Kill(); $wp.WaitForExit(5000) | Out-Null } }
$p = Start-Process (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$t\uninstall.log" -Wait -PassThru
Check 'uninstall.exit' ($p.ExitCode -eq 0) "code=$($p.ExitCode)"
$deadline = (Get-Date).AddSeconds(15); while ((Get-Date) -lt $deadline -and (Test-Path $app)) { Start-Sleep 1 }
Check 'uninstall.dir' (-not (Test-Path $app))
Check 'uninstall.shortcuts' (-not (Test-Path $lnk) -and -not (Test-Path $desk))
Check 'uninstall.entry' (@(Get-ItemProperty $reg -EA SilentlyContinue | Where-Object DisplayName -like 'WinVM*').Count -eq 0)
Write-Host "RESULT failed=$fail"
exit $fail
