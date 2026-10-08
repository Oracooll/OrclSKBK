# Removes OrclSKBK (and anything left by its pre-1.3 name, "Surface Keyboard Backlight Keeper") for the current user,
# and reports anything it could not remove:
#   - stops running copies of the app
#   - removes the start-at-sign-in entries and Start menu shortcuts
#   - deletes the installed exe, the app's log files and its settings
# Only files the app itself creates are deleted. Folders are removed only when nothing else is left in them.
# Exit code 0 = fully removed, 1 = something is left (listed in the output).
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
$ErrorActionPreference = 'Stop'
$products  = @('OrclSKBK', 'Surface Keyboard Backlight Keeper')
$runKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runNames  = @('OrclSKBK', 'SurfaceBacklightKeeper')
$programs  = [Environment]::GetFolderPath('Programs')
$shortcuts = @((Join-Path $programs 'OrclSKBK.lnk'), (Join-Path $programs 'Surface Keyboard Backlight Keeper.lnk'))
$appDir    = Join-Path $env:LOCALAPPDATA 'OrclSKBK'                         # exe and logs
$oldExeDir = Join-Path $env:LOCALAPPDATA 'SurfaceKeyboardBacklightKeeper'   # pre-1.3 exe
$oldLogDir = Join-Path $env:LOCALAPPDATA 'SurfaceBacklightKeeper'           # pre-1.3 logs
$appFiles  = @(
    (Join-Path $appDir 'OrclSKBK.exe'), (Join-Path $appDir 'keeper.log'), (Join-Path $appDir 'keeper.old.log'),
    (Join-Path $oldExeDir 'SurfaceKeyboardBacklightKeeper.exe'),
    (Join-Path $oldLogDir 'keeper.log'), (Join-Path $oldLogDir 'keeper.old.log'))
$folders      = @($appDir, $oldExeDir, $oldLogDir)
$settingsKeys = @('HKCU:\Software\OrclSKBK', 'HKCU:\Software\SurfaceBacklightKeeper')

$done     = New-Object 'System.Collections.Generic.List[string]'
$problems = New-Object 'System.Collections.Generic.List[string]'
$notes    = New-Object 'System.Collections.Generic.List[string]'

function Invoke-Step([string]$what, [scriptblock]$action) {
    try { & $action; $done.Add($what) } catch { $problems.Add("$what failed: $($_.Exception.Message)") }
}

# Matched by the product name embedded in the exe, so an unrelated program with the same process name is never touched.
function Get-AppProcesses {
    @(Get-Process -Name 'OrclSKBK', 'SurfaceKeyboardBacklightKeeper' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($products -contains (Get-Item -LiteralPath $_.Path).VersionInfo.ProductName) } catch { $false }
    })
}
function Test-RunEntry([string]$name) { [bool](Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue) }

# 1. Running copies
foreach ($p in Get-AppProcesses) {
    $proc = $p
    Invoke-Step "Stopped the copy running from $($proc.Path)" {
        Stop-Process -Id $proc.Id -Force
        if (-not $proc.WaitForExit(5000)) { throw 'it did not exit within 5 s' }
    }
}

# 2. Start-at-sign-in entries and Start menu shortcuts
foreach ($n in $runNames) { $name = $n; if (Test-RunEntry $name) { Invoke-Step "Removed the start-at-sign-in entry '$name'" { Remove-ItemProperty -Path $runKey -Name $name } } }
foreach ($s in $shortcuts) { $lnk = $s; if (Test-Path -LiteralPath $lnk) { Invoke-Step "Removed the Start menu shortcut $lnk" { Remove-Item -LiteralPath $lnk -Force } } }

# 3. Files the app creates, then their folders if nothing else is in them
foreach ($f in $appFiles) { $file = $f; if (Test-Path -LiteralPath $file) { Invoke-Step "Deleted $file" { Remove-Item -LiteralPath $file -Force } } }
foreach ($d in $folders) {
    $dir = $d
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $left = @(Get-ChildItem -LiteralPath $dir -Force)
    if ($left.Count -eq 0) { Invoke-Step "Deleted the empty folder $dir" { Remove-Item -LiteralPath $dir -Force } }
    else { $notes.Add("Kept $dir because it also contains files this app did not create: " + (($left | ForEach-Object { $_.Name }) -join ', ')) }
}

# 4. Settings (only values, no subkeys, are expected; anything else is left alone)
foreach ($k in $settingsKeys) {
    $key = $k
    if (Test-Path $key) {
        Invoke-Step "Deleted the settings ($($key.Replace('HKCU:', 'HKCU')))" {
            if (@(Get-ChildItem -Path $key).Count -gt 0) { throw 'the key has subkeys this app did not create, so it was left in place' }
            Remove-Item -Path $key -Force
        }
    }
}

# 5. Verify
$still = New-Object 'System.Collections.Generic.List[string]'
foreach ($p in Get-AppProcesses) { $still.Add("a running copy ($($p.Path))") }
foreach ($n in $runNames) { if (Test-RunEntry $n) { $still.Add("the start-at-sign-in entry '$n'") } }
foreach ($s in $shortcuts) { if (Test-Path -LiteralPath $s) { $still.Add($s) } }
foreach ($f in $appFiles) { if (Test-Path -LiteralPath $f) { $still.Add($f) } }
foreach ($k in $settingsKeys) { if (Test-Path $k) { $still.Add("the settings key $k") } }

foreach ($line in $done) { Write-Host $line }
foreach ($line in $notes) { Write-Host "Note: $line" }
foreach ($line in $problems) { Write-Warning $line }
if ($still.Count -eq 0) {
    Write-Host 'OrclSKBK is removed. The keyboard backlight now behaves as Windows ships it.'
    exit 0
}
Write-Warning ('Still present: ' + ($still -join '; '))
exit 1
