# Removes the installed app, its Start Menu shortcut and its "Start with Windows" entry.
# Keeps your progress in %LOCALAPPDATA%\Quizzler unless you pass -RemoveData.
#   powershell -ExecutionPolicy Bypass -File tools/uninstall.ps1
param([switch]$RemoveData, [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Quizzler'))

Get-Process Quizzler.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Remove the toast-notification registration (must run from the installed exe; it exits immediately).
$exe = Join-Path $Destination 'Quizzler.App.exe'
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--unregister-notifications' -Wait }

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Quizzler' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Quizzler.lnk') -ErrorAction SilentlyContinue
Remove-Item $Destination -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'Quizzler') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed app and data."
} else {
    Write-Host "Removed app. Progress kept in $(Join-Path $env:LOCALAPPDATA 'Quizzler')."
}
