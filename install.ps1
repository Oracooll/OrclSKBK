# Installs Surface Keyboard Backlight Keeper for the current user (no admin rights needed):
#   - copies the exe to %LOCALAPPDATA%\SurfaceKeyboardBacklightKeeper
#   - registers it to start at sign-in and adds a Start menu shortcut
#   - starts it
# Run from the folder that contains SurfaceKeyboardBacklightKeeper.exe (the release zip, or the repo after build.ps1):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'
$product = 'Surface Keyboard Backlight Keeper'
$exeName = 'SurfaceKeyboardBacklightKeeper.exe'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$src = Join-Path $here $exeName
if (-not (Test-Path -LiteralPath $src)) { $src = Join-Path $here "build\$exeName" }
if (-not (Test-Path -LiteralPath $src)) { throw "$exeName not found next to this script or in .\build. Run build.ps1 first or use the release zip." }
if ((Get-Item -LiteralPath $src).VersionInfo.ProductName -ne $product) { throw "$src is not $product." }

$dest = Join-Path $env:LOCALAPPDATA 'SurfaceKeyboardBacklightKeeper'
$installed = Join-Path $dest $exeName

# Running copies of this app, wherever they run from (only one instance can run at a time). Matched by the product
# name embedded in the exe, so an unrelated program that happens to use the same process name is never touched.
function Get-AppProcesses {
    @(Get-Process -Name 'SurfaceKeyboardBacklightKeeper' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ((Get-Item -LiteralPath $_.Path).VersionInfo.ProductName -eq $product) } catch { $false }
    })
}

foreach ($p in Get-AppProcesses) {
    Write-Host "Stopping the copy running from $($p.Path)"
    Stop-Process -Id $p.Id -Force
    if (-not $p.WaitForExit(5000)) { throw "The copy running from $($p.Path) did not exit." }
}

New-Item -ItemType Directory -Force $dest | Out-Null
if ([IO.Path]::GetFullPath($src) -ne [IO.Path]::GetFullPath($installed)) {
    Copy-Item -LiteralPath $src -Destination $installed -Force
    if ((Get-FileHash -LiteralPath $src).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw "Copying to $installed did not complete." }
}

# Same Run-key value name the app itself uses for its "Start with Windows" menu item.
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SurfaceBacklightKeeper' -Value ('"' + $installed + '"')

# Start menu shortcut, so it can be launched by typing its name in Start.
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Keyboard Backlight Keeper.lnk'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $installed; $sc.WorkingDirectory = $dest; $sc.Description = 'Keeps the Surface keyboard backlight on'; $sc.Save()

Start-Process -FilePath $installed
Start-Sleep -Seconds 2
$version = (Get-Item -LiteralPath $installed).VersionInfo.FileVersion
if (Get-AppProcesses | Where-Object { $_.Path -eq $installed }) {
    Write-Host "Installed version $version to $installed, set to start at sign-in, and added to the Start menu. Look for the keyboard icon in the tray."
} else {
    Write-Warning "Installed version $version to $installed, but it did not stay running. Tick 'Write log file' in its menu after starting it from the Start menu, or check %LOCALAPPDATA%\SurfaceBacklightKeeper\keeper.log for a crash report."
    exit 1
}
