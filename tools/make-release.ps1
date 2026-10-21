# ============================================================
#  make-release.ps1  --  build a single-file release
# ------------------------------------------------------------
#  Compresses adb.exe / AdbWinApi.dll / AdbWinUsbApi.dll /
#  NOTICE.txt with GZip and embeds them into the exe.
#  The user downloads ONE file, double-clicks it, done.
#
#  Usage:
#      pwsh -File tools\make-release.ps1
#      pwsh -File tools\make-release.ps1 -PlatformTools "D:\android\platform-tools"
#
#  Output:
#      dist\PhoneAlbum-v<version>-win64.exe
# ============================================================

param(
    [string]$Version = '1.2.0',
    [string]$PlatformTools = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression | Out-Null

$repoRoot  = Split-Path -Parent $PSScriptRoot
$srcDir    = Join-Path $repoRoot 'src'
$distRoot  = Join-Path $repoRoot 'dist'
$stageDir  = Join-Path $distRoot '_stage'

function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }

# ---------- 1. compiler ----------
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# ---------- 2. locate platform-tools ----------
if (-not $PlatformTools) {
    $candidates = @()
    $pkg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path $pkg) {
        Get-ChildItem $pkg -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*PlatformTools*' } |
            ForEach-Object {
                $pt = Join-Path $_.FullName 'platform-tools'
                if (Test-Path (Join-Path $pt 'adb.exe')) { $candidates += $pt }
            }
    }
    $c = Get-Command adb -ErrorAction SilentlyContinue
    if ($c) { $candidates += (Split-Path $c.Source -Parent) }
    foreach ($p in @((Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools'), 'C:\platform-tools')) {
        if (Test-Path (Join-Path $p 'adb.exe')) { $candidates += $p }
    }
    foreach ($cand in $candidates) {
        if ((Test-Path (Join-Path $cand 'adb.exe')) -and (Test-Path (Join-Path $cand 'NOTICE.txt'))) {
            $PlatformTools = $cand; break
        }
    }
    if (-not $PlatformTools -and $candidates.Count -gt 0) { $PlatformTools = $candidates[0] }
}
if (-not $PlatformTools -or -not (Test-Path (Join-Path $PlatformTools 'adb.exe'))) {
    throw 'platform-tools not found. Download from https://developer.android.com/tools/releases/platform-tools and pass -PlatformTools.'
}
Ok ("platform-tools: " + $PlatformTools)

# ---------- 3. compress resources ----------
Info "Compressing embedded resources..."
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

$resMap = @(
    @{ Src = (Join-Path $PlatformTools 'adb.exe');          Res = 'res_adb.gz';          Name = 'adb.exe' },
    @{ Src = (Join-Path $PlatformTools 'AdbWinApi.dll');    Res = 'res_adbwinapi.gz';    Name = 'AdbWinApi.dll' },
    @{ Src = (Join-Path $PlatformTools 'AdbWinUsbApi.dll'); Res = 'res_adbwinusb.gz';    Name = 'AdbWinUsbApi.dll' },
    @{ Src = (Join-Path $PlatformTools 'NOTICE.txt');       Res = 'res_notice.gz';       Name = 'NOTICE.txt' },
    @{ Src = (Join-Path $repoRoot 'tools\fix-adb.ps1');     Res = 'res_fix.gz';          Name = 'fix-adb.ps1' }
)

$resArgs = @()
$rawTotal = 0
$gzTotal  = 0
foreach ($m in $resMap) {
    $src = $m.Src
    if (-not (Test-Path $src)) { Write-Host ("  skip (missing): " + $m.Name) -ForegroundColor Yellow; continue }
    $dst = Join-Path $stageDir $m.Res

    $in  = [System.IO.File]::OpenRead($src)
    $out = [System.IO.File]::Create($dst)
    $gz  = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionMode]::Compress)
    try { $in.CopyTo($gz) } finally { $gz.Dispose(); $out.Dispose(); $in.Dispose() }

    $raw = (Get-Item $src).Length
    $cmp = (Get-Item $dst).Length
    $rawTotal += $raw; $gzTotal += $cmp
    Write-Host ("  {0,-18} {1,8:N2} MB -> {2,8:N2} MB" -f $m.Name, ($raw/1MB), ($cmp/1MB))
    $resArgs += ('/resource:"' + $dst + '",' + $m.Res)
}
Write-Host ("  {0,-18} {1,8:N2} MB -> {2,8:N2} MB" -f 'TOTAL', ($rawTotal/1MB), ($gzTotal/1MB))

# ---------- 4. compile ----------
Info "Compiling single-file exe..."
if (-not (Test-Path $distRoot)) { New-Item -ItemType Directory -Force -Path $distRoot | Out-Null }
$outExe = Join-Path $distRoot "PhoneAlbum-v$Version-win64.exe"
if (Test-Path $outExe) { Remove-Item $outExe -Force }

$argLine = @(
    '/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
    ('/win32manifest:"' + (Join-Path $srcDir 'app.manifest') + '"'),
    ('/out:"' + $outExe + '"'),
    '/r:System.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    '/r:System.Core.dll', '/r:Microsoft.CSharp.dll'
) + $resArgs + @('"' + (Join-Path $srcDir 'PhoneAlbum.cs') + '"')

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $csc
$psi.Arguments = ($argLine -join ' ')
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::GetEncoding(936)
$psi.StandardErrorEncoding = [System.Text.Encoding]::GetEncoding(936)

$p = [System.Diagnostics.Process]::Start($psi)
$outText = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
$p.WaitForExit()
if ($outText.Trim()) { Write-Host $outText.Trim() }

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $outExe)) { throw 'compile failed' }

$fi = Get-Item $outExe
Write-Host ''
Ok '========== DONE =========='
Ok ("output: " + $fi.FullName)
Ok ("size:   " + [math]::Round($fi.Length / 1MB, 2) + " MB")
Write-Host ''
Write-Host 'User downloads this single file, double-clicks, done. No unzip, no install.' -ForegroundColor Gray
Write-Host ('adb will be extracted to: ' + (Join-Path $env:LOCALAPPDATA 'PhoneAlbum\platform-tools')) -ForegroundColor Gray