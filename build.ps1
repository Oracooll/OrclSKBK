# Rebuilds OrclSKBK.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# No Visual Studio or .NET SDK required. Run from anywhere:  powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$out  = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$out\OrclSKBK.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/win32manifest:$src\app.manifest" `
    "$src\OrclSKBK.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Built $out\OrclSKBK.exe"
