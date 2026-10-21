# ============================================================
#  make-release.ps1  —  组装可分发的发布包
#  ------------------------------------------------------------
#  用法：
#      pwsh -File tools\make-release.ps1
#      pwsh -File tools\make-release.ps1 -PlatformTools "D:\android\platform-tools"
#
#  产物：
#      dist\PhoneAlbum-v<版本>-win64\        解压即用的目录
#      dist\PhoneAlbum-v<版本>-win64.zip     发布用压缩包
# ============================================================

param(
    [string]$Version = '1.1.0',
    [string]$PlatformTools = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $repoRoot 'src'
$distRoot = Join-Path $repoRoot 'dist'
$stageName = "PhoneAlbum-v$Version-win64"
$stage = Join-Path $distRoot $stageName

function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Warn($m) { Write-Host $m -ForegroundColor Yellow }

# ---------- 1. 定位 csc ----------
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "找不到 csc.exe (.NET Framework 4.x 未安装): $csc"
}

# ---------- 2. 编译 ----------
Info "编译中..."
$exeOut = Join-Path $srcDir 'PhoneAlbum.exe'
if (Test-Path $exeOut) { Remove-Item $exeOut -Force }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $csc
$psi.Arguments = '/nologo /target:winexe /optimize+ /codepage:65001 /win32manifest:"' +
    (Join-Path $srcDir 'app.manifest') + '" /out:"' + $exeOut + '" ' +
    '/r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll "' +
    (Join-Path $srcDir 'PhoneAlbum.cs') + '"'
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
if (-not (Test-Path $exeOut)) { throw '编译失败' }
Ok ("编译成功: " + [math]::Round((Get-Item $exeOut).Length / 1KB, 1) + " KB")

# ---------- 3. 定位 platform-tools ----------
if (-not $PlatformTools) {
    $candidates = @()

    # 优先：WinGet 安装的正式 platform-tools 包（带 NOTICE.txt，便于合规分发）
    $pkg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path $pkg) {
        $hits = Get-ChildItem $pkg -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*PlatformTools*' }
        foreach ($h in $hits) {
            $pt = Join-Path $h.FullName 'platform-tools'
            if (Test-Path (Join-Path $pt 'adb.exe')) { $candidates += $pt }
        }
    }

    # 其次：PATH 上的 adb（可能来自 scrcpy 等附带版本）
    $c = Get-Command adb -ErrorAction SilentlyContinue
    if ($c) { $candidates += (Split-Path $c.Source -Parent) }

    # 常见 SDK 路径
    foreach ($p in @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools'),
        'C:\platform-tools'
    )) {
        if (Test-Path (Join-Path $p 'adb.exe')) { $candidates += $p }
    }

    foreach ($cand in $candidates) {
        if ((Test-Path (Join-Path $cand 'adb.exe')) -and (Test-Path (Join-Path $cand 'NOTICE.txt'))) {
            $PlatformTools = $cand
            break
        }
    }
    if (-not $PlatformTools -and $candidates.Count -gt 0) {
        $PlatformTools = $candidates[0]
    }
}
if (-not $PlatformTools -or -not (Test-Path (Join-Path $PlatformTools 'adb.exe'))) {
    throw '找不到 platform-tools。请下载 https://developer.android.com/tools/releases/platform-tools 后，用 -PlatformTools 指定目录。'
}
Ok ("platform-tools: " + $PlatformTools)

# ---------- 4. 组装 ----------
Info "组装发布目录..."
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $exeOut (Join-Path $stage 'PhoneAlbum.exe') -Force

$ptDst = Join-Path $stage 'platform-tools'
New-Item -ItemType Directory -Force -Path $ptDst | Out-Null
foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt', 'source.properties') {
    $s = Join-Path $PlatformTools $f
    if (Test-Path $s) { Copy-Item $s (Join-Path $ptDst $f) -Force }
}

Copy-Item (Join-Path $repoRoot 'tools\fix-adb.ps1') (Join-Path $stage 'fix-adb.ps1') -Force
Copy-Item (Join-Path $repoRoot 'tools\修复ADB连接.bat') (Join-Path $stage '修复ADB连接.bat') -Force

# 使用说明
$readme = @"
手机相册传输  v$Version
==================================================

【这是什么】
在电脑上按手机相册的视角浏览照片视频，勾选需要的再传，
不用把几十 GB 全量同步一遍。


【第一次使用】

1. 插上数据线，手机通知栏选择「传输文件」
   （不要选「仅充电」或「传输照片」）

2. 双击 PhoneAlbum.exe
   这时已经能用了（MTP 模式）

3. 想更好用就再开一下 USB 调试（可选，只需一次）
   设置 -> 关于手机 -> 连点「版本号」7 次
   设置 -> 系统和更新 -> 开发人员选项 -> 打开「USB 调试」
   重插数据线，手机弹「允许 USB 调试吗」-> 勾「始终允许」-> 允许
   重开程序，这时是 ADB 模式：更快、有拍摄时间、有分辨率

   两种模式对照：
     ADB 模式  ~37 MB/s  时间准确  有分辨率/时长
     MTP 模式  ~28 MB/s  时间未知  无分辨率/时长
   程序会自动选，不用你操心。


【看不到手机怎么办】

按顺序排查：

  1) 换一根数据线（很多线只能充电，不能传数据）
  2) 确认手机选的是「传输文件」
  3) 用命令行试一下：
       platform-tools\adb.exe devices
     能列出设备说明连接正常；列不出来就说没连上

  4) 如果 adb devices 是空的，但设备管理器里
     「ADB Interface」状态正常 ——
     双击「修复ADB连接.bat」（需要管理员）
     它会修 Windows 通用驱动缺失 adb 接口 GUID 的问题。
     修完重开程序。


【使用】

  左侧        选相册（每个相册标了数量和体积）
  右侧        看文件（文件名/类型/大小/时间/分辨率/时长）
              点列头可以排序
  右键相册    传输整个相册
  多选文件    传输所选（Ctrl 点选，Shift 连选，Ctrl+A 全选）
  目标        可改，默认 图片\手机相册

已存在的问题会自动跳过，可以反复点。


【看不到哪些文件】

只读取系统媒体索引，所以以下内容看不到：
  - App 私有目录里的文件
  - 未保存到相册的微信聊天图片
  - 文档、压缩包等非媒体文件

微信图片请先在聊天里长按「保存到手机」。


【环境要求】

  Windows 10 1903+ / Windows 11
  .NET Framework 4.8（系统自带）


【许可】

  程序代码：MIT
  内置 adb：Apache 2.0（Android Open Source Project）
  详见 platform-tools\NOTICE.txt
"@
[System.IO.File]::WriteAllText((Join-Path $stage '使用说明.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------- 5. 打包 ----------
Info "打包中..."
$zip = Join-Path $distRoot "$stageName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$zipItem = Get-Item $zip
Write-Host ''
Ok '========== 完成 =========='
Ok ("目录: " + $stage)
Ok ("压缩包: " + $zip)
Ok ("大小: " + [math]::Round($zipItem.Length / 1MB, 2) + " MB")
