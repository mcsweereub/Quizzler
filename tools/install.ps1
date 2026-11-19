# Builds Quizzler and installs it for the current user (no admin needed):
#   app -> %LOCALAPPDATA%\Programs\Quizzler\Quizzler.App.exe   (+ Start Menu shortcut)
#   cli -> %LOCALAPPDATA%\Programs\Quizzler\cli\quizzler.exe
# Re-run any time to update; your progress (%LOCALAPPDATA%\Quizzler\quizzler.db) is untouched.
# On first launch the installed app turns on "Start with Windows" (toggle it from the tray menu).
#
#   powershell -ExecutionPolicy Bypass -File tools/install.ps1
#   powershell -ExecutionPolicy Bypass -File tools/install.ps1 -SelfContained   # no .NET install needed (for sharing)
param([switch]$SelfContained, [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Quizzler'))

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

Write-Host "Stopping any running Quizzler..."
Get-Process Quizzler.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$common = @('-c', 'Release', '--nologo', '-v', 'quiet')
# The app always bundles the Windows App SDK runtime (for toasts); -SelfContained also bundles .NET itself.
$dotnetMode = if ($SelfContained) { @('-r', 'win-x64', '--self-contained', 'true') } else { @('--self-contained', 'false') }

Write-Host "Publishing app to $Destination ..."
dotnet publish "$root\src\Quizzler.App" @common @dotnetMode -o $Destination
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Write-Host "Publishing CLI to $Destination\cli ..."
$cliMode = if ($SelfContained) { @('-r', 'win-x64', '--self-contained', 'true') } else { @() }
dotnet publish "$root\src\Quizzler.Cli" @common @cliMode -o (Join-Path $Destination 'cli')
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

$exe = Join-Path $Destination 'Quizzler.App.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Quizzler.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $Destination
$link.IconLocation = "$exe,0"
$link.Description = 'Spaced-repetition quizzes for software engineering'
$link.Save()
Write-Host "Start Menu shortcut: $shortcut"

Write-Host "Launching Quizzler..."
Start-Process $exe
Write-Host "Done. Look for the Q icon in the system tray (it may be under the ^ overflow arrow)."
