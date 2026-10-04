# Removes Surface Keyboard Backlight Keeper for the current user and reports anything it could not remove:
#   - stops running copies of the app
#   - removes the start-at-sign-in entry and the Start menu shortcut
#   - deletes the installed exe, the app's log files and its settings
# Only files the app itself creates are deleted. Folders are removed only when nothing else is left in them.
# Exit code 0 = fully removed, 1 = something is left (listed in the output).
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
$ErrorActionPreference = 'Stop'
$product     = 'Surface Keyboard Backlight Keeper'
$exeName     = 'SurfaceKeyboardBacklightKeeper.exe'
$installDir  = Join-Path $env:LOCALAPPDATA 'SurfaceKeyboardBacklightKeeper'
$dataDir     = Join-Path $env:LOCALAPPDATA 'SurfaceBacklightKeeper'
$settingsKey = 'HKCU:\Software\SurfaceBacklightKeeper'
$runKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName     = 'SurfaceBacklightKeeper'
$shortcut    = Join-Path ([Environment]::GetFolderPath('Programs')) 'Surface Keyboard Backlight Keeper.lnk'
$appFiles    = @((Join-Path $installDir $exeName), (Join-Path $dataDir 'keeper.log'), (Join-Path $dataDir 'keeper.old.log'))

$done     = New-Object 'System.Collections.Generic.List[string]'
$problems = New-Object 'System.Collections.Generic.List[string]'
$notes    = New-Object 'System.Collections.Generic.List[string]'

function Invoke-Step([string]$what, [scriptblock]$action) {
    try { & $action; $done.Add($what) } catch { $problems.Add("$what failed: $($_.Exception.Message)") }
}

# Matched by the product name embedded in the exe, so an unrelated program with the same process name is never touched.
function Get-AppProcesses {
    @(Get-Process -Name 'SurfaceKeyboardBacklightKeeper' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ((Get-Item -LiteralPath $_.Path).VersionInfo.ProductName -eq $product) } catch { $false }
    })
}
function Test-RunEntry { [bool](Get-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue) }

# 1. Running copies
foreach ($p in Get-AppProcesses) {
    $proc = $p
    Invoke-Step "Stopped the copy running from $($proc.Path)" {
        Stop-Process -Id $proc.Id -Force
        if (-not $proc.WaitForExit(5000)) { throw 'it did not exit within 5 s' }
    }
}

# 2. Start-at-sign-in entry and Start menu shortcut
if (Test-RunEntry) { Invoke-Step 'Removed the start-at-sign-in entry' { Remove-ItemProperty -Path $runKey -Name $runName } }
if (Test-Path -LiteralPath $shortcut) { Invoke-Step 'Removed the Start menu shortcut' { Remove-Item -LiteralPath $shortcut -Force } }

# 3. Files the app creates, then their folders if nothing else is in them
foreach ($f in $appFiles) {
    $file = $f
    if (Test-Path -LiteralPath $file) { Invoke-Step "Deleted $file" { Remove-Item -LiteralPath $file -Force } }
}
foreach ($d in @($installDir, $dataDir)) {
    $dir = $d
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $left = @(Get-ChildItem -LiteralPath $dir -Force)
    if ($left.Count -eq 0) { Invoke-Step "Deleted the empty folder $dir" { Remove-Item -LiteralPath $dir -Force } }
    else { $notes.Add("Kept $dir because it also contains files this app did not create: " + (($left | ForEach-Object { $_.Name }) -join ', ')) }
}

# 4. Settings (only values, no subkeys, are expected; anything else is left alone)
if (Test-Path $settingsKey) {
    Invoke-Step 'Deleted the settings (HKCU\Software\SurfaceBacklightKeeper)' {
        if (@(Get-ChildItem -Path $settingsKey).Count -gt 0) { throw 'the key has subkeys this app did not create, so it was left in place' }
        Remove-Item -Path $settingsKey -Force
    }
}

# 5. Verify
$still = New-Object 'System.Collections.Generic.List[string]'
foreach ($p in Get-AppProcesses) { $still.Add("a running copy ($($p.Path))") }
if (Test-RunEntry) { $still.Add('the start-at-sign-in entry') }
if (Test-Path -LiteralPath $shortcut) { $still.Add('the Start menu shortcut') }
foreach ($f in $appFiles) { if (Test-Path -LiteralPath $f) { $still.Add($f) } }
if (Test-Path $settingsKey) { $still.Add('the settings key') }

foreach ($line in $done) { Write-Host $line }
foreach ($line in $notes) { Write-Host "Note: $line" }
foreach ($line in $problems) { Write-Warning $line }
if ($still.Count -eq 0) {
    Write-Host 'Surface Keyboard Backlight Keeper is removed. The keyboard backlight now behaves as Windows ships it.'
    exit 0
}
Write-Warning ('Still present: ' + ($still -join '; '))
exit 1
