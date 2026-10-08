# Builds, zips and publishes a GitHub release with the locally built (and tested) binary.
# Requires the GitHub CLI (gh) signed in. The version (1.X.XXX) comes from AppInfo.Version in the source,
# embedded in the exe as its product version. Release title "OrclSKBK 1.X.XXX", tag "v1.X.XXX".
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 [-Notes "what changed"]
param([string]$Notes = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'build\OrclSKBK.exe'

# The build overwrites the exe, so stop a copy running from the build folder first and start it again afterwards.
# Copies running from anywhere else (for example the installed one) are left alone.
$running = @(Get-Process -Name OrclSKBK -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
foreach ($p in $running) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(5000) | Out-Null }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
if ($running.Count -gt 0) { Start-Process -FilePath $exe }

$version = (Get-Item $exe).VersionInfo.ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d{3}$') { throw "Unexpected version '$version' (expected 1.X.XXX)." }
$tag = "v$version"
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "OrclSKBK-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $exe, (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$hashFile = "$zip.sha256"
"$hash  $(Split-Path $zip -Leaf)`n$exeHash  OrclSKBK.exe" | Set-Content $hashFile -Encoding ascii

if ($Notes -eq "") { $Notes = "Release $version." }
$body = @"
$Notes

**SHA-256**
- ``$(Split-Path $zip -Leaf)``: ``$hash``
- ``OrclSKBK.exe``: ``$exeHash``

Made by Claude, prompted by Oracooll. Unsigned binary: Windows SmartScreen will warn on first run; choose *More info* > *Run anyway*, or build it yourself with ``build.ps1`` (uses the C# compiler that ships with Windows).
"@
gh release create $tag $exe $zip $hashFile --title "OrclSKBK $version" --notes $body
Write-Host "Published OrclSKBK $version ($tag)"

# Local layout: the repo lives in <app folder>\OrclSKBK-src and the released exe sits in <app folder> itself.
$appFolder = Split-Path -Parent $root
if ((Split-Path -Leaf $root) -eq 'OrclSKBK-src') {
    try { Copy-Item -LiteralPath $exe -Destination (Join-Path $appFolder 'OrclSKBK.exe') -Force; Write-Host "Copied the released exe to $appFolder" }
    catch { Write-Warning "Could not copy the released exe to ${appFolder}: $($_.Exception.Message) (close OrclSKBK if it runs from there)." }
}
