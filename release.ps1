# Builds, zips and publishes a GitHub release with the locally built (and tested) binary.
# Requires the GitHub CLI (gh) signed in. Version comes from the exe's file version.
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 [-Notes "what changed"]
param([string]$Notes = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'build\SurfaceKeyboardBacklightKeeper.exe'

# The build overwrites the exe, so stop a copy running from the build folder first and start it again afterwards.
# Copies running from anywhere else (for example the installed one) are left alone.
$running = @(Get-Process -Name SurfaceKeyboardBacklightKeeper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
foreach ($p in $running) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(5000) | Out-Null }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
if ($running.Count -gt 0) { Start-Process -FilePath $exe }
$v = (Get-Item $exe).VersionInfo.FileVersion.Split('.')
$tag = "v$($v[0]).$($v[1]).$($v[2])"
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "SurfaceKeyboardBacklightKeeper-$tag.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $exe, (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$hashFile = "$zip.sha256"
"$hash  $(Split-Path $zip -Leaf)" | Set-Content $hashFile -Encoding ascii

if ($Notes -eq "") { $Notes = "Release $tag." }
$body = @"
$Notes

**SHA-256** of ``$(Split-Path $zip -Leaf)``: ``$hash``

Made by Claude, prompted by Oracooll. Unsigned binary: Windows SmartScreen will warn on first run; choose *More info* > *Run anyway*, or build it yourself with ``build.ps1`` (uses the C# compiler that ships with Windows).
"@
gh release create $tag $zip $hashFile --title "Surface Keyboard Backlight Keeper $tag" --notes $body
Write-Host "Published $tag"
