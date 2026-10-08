# Installs OrclSKBK (Oracooll Surface Keyboard Backlight Keeper) for the current user, no admin rights needed:
#   - copies OrclSKBK.exe to %LOCALAPPDATA%\OrclSKBK
#   - registers it to start at sign-in and adds a Start menu shortcut
#   - upgrades from the pre-1.3 "Surface Keyboard Backlight Keeper": stops it and removes its startup entry,
#     shortcut and installed exe (its settings are carried over by OrclSKBK on first start)
#   - starts it
# Run from the folder that contains OrclSKBK.exe (the release zip, or the repo after build.ps1):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'
$product = 'OrclSKBK'
$legacyProduct = 'Surface Keyboard Backlight Keeper'
$exeName = 'OrclSKBK.exe'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$programs = [Environment]::GetFolderPath('Programs')

$src = Join-Path $here $exeName
if (-not (Test-Path -LiteralPath $src)) { $src = Join-Path $here "build\$exeName" }
if (-not (Test-Path -LiteralPath $src)) { throw "$exeName not found next to this script or in .\build. Run build.ps1 first or use the release zip." }
if ((Get-Item -LiteralPath $src).VersionInfo.ProductName -ne $product) { throw "$src is not $product." }

$dest = Join-Path $env:LOCALAPPDATA 'OrclSKBK'
$installed = Join-Path $dest $exeName

# Running copies of this app or its pre-1.3 version, wherever they run from (only one may run at a time). Matched by the
# product name embedded in the exe, so an unrelated program that happens to use the same process name is never touched.
function Get-AppProcesses {
    @(Get-Process -Name 'OrclSKBK', 'SurfaceKeyboardBacklightKeeper' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and (@($product, $legacyProduct) -contains (Get-Item -LiteralPath $_.Path).VersionInfo.ProductName) } catch { $false }
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
Set-ItemProperty -Path $runKey -Name 'OrclSKBK' -Value ('"' + $installed + '"')

# Start menu shortcut, so it can be launched by typing its name in Start.
$lnk = Join-Path $programs 'OrclSKBK.lnk'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $installed; $sc.WorkingDirectory = $dest; $sc.Description = 'Oracooll Surface Keyboard Backlight Keeper: keeps the keyboard backlight on'; $sc.Save()

# Remove what the pre-1.3 version installed. Its settings stay until uninstall.ps1, so OrclSKBK can copy them.
if (Get-ItemProperty -Path $runKey -Name 'SurfaceBacklightKeeper' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'SurfaceBacklightKeeper'; Write-Host 'Removed the old version''s start-at-sign-in entry'
}
$oldLnk = Join-Path $programs 'Surface Keyboard Backlight Keeper.lnk'
if (Test-Path -LiteralPath $oldLnk) { Remove-Item -LiteralPath $oldLnk -Force; Write-Host 'Removed the old version''s Start menu shortcut' }
$oldDir = Join-Path $env:LOCALAPPDATA 'SurfaceKeyboardBacklightKeeper'
$oldExe = Join-Path $oldDir 'SurfaceKeyboardBacklightKeeper.exe'
if (Test-Path -LiteralPath $oldExe) {
    Remove-Item -LiteralPath $oldExe -Force; Write-Host "Removed the old version's exe ($oldExe)"
    if (@(Get-ChildItem -LiteralPath $oldDir -Force).Count -eq 0) { Remove-Item -LiteralPath $oldDir -Force }
}

Start-Process -FilePath $installed
Start-Sleep -Seconds 2
$version = (Get-Item -LiteralPath $installed).VersionInfo.ProductVersion
if (Get-AppProcesses | Where-Object { $_.Path -eq $installed }) {
    Write-Host "Installed OrclSKBK $version to $installed, set to start at sign-in, and added to the Start menu. Look for the keyboard icon in the tray."
} else {
    Write-Warning "Installed OrclSKBK $version to $installed, but it did not stay running. Check %LOCALAPPDATA%\OrclSKBK\keeper.log for a crash report."
    exit 1
}
