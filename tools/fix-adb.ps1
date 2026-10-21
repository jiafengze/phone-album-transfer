# ============================================================
#  修复 ADB 连接
#  ------------------------------------------------------------
#  现象：手机已插好、USB 调试已开、设备管理器里能看到
#        「ADB Interface」且状态正常，但程序仍提示「没检测到手机」。
#
#  原因：Windows 10/11 会给 ADB 接口自动绑定通用 WinUSB 驱动，
#        但这个通用驱动不会注册 adb 用来枚举设备的接口 GUID
#        {F72FE0D4-CBCB-407D-8814-9ED673D0DD6B}，
#        于是 adb 完全看不到这台设备。
#
#  本脚本：给当前插着的 ADB 接口补上该 GUID，然后重启该接口。
#        注册表改动是持久的，同一个 USB 口只需修一次。
# ============================================================

$ErrorActionPreference = 'Continue'
$AdbGuid = '{F72FE0D4-CBCB-407D-8814-9ED673D0DD6B}'

# ---------- 自提权 ----------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '需要管理员权限，正在申请提升...' -ForegroundColor Yellow
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $PSCommandPath + '"')
        ) | Out-Null
    } catch {
        Write-Host '提权被拒绝，无法继续。' -ForegroundColor Red
        Read-Host '按回车关闭'
    }
    exit
}

Write-Host '======================================' -ForegroundColor Cyan
Write-Host ' 修复 ADB 连接' -ForegroundColor Cyan
Write-Host '======================================' -ForegroundColor Cyan
Write-Host ''

Write-Host '正在查找 ADB 接口...' -ForegroundColor Gray
$candidates = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }

$found = @()
foreach ($d in $candidates) {
    $compat = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_CompatibleIds' -ErrorAction SilentlyContinue).Data
    if ($compat -and ($compat -contains 'USB\Class_ff&SubClass_42&Prot_01')) {
        $found += $d
    }
}

if ($found.Count -eq 0) {
    Write-Host '没有找到 ADB 接口。' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '请依次确认：' -ForegroundColor Gray
    Write-Host '  1. 数据线支持传输文件（不是充电线）'
    Write-Host '  2. 手机通知栏选的是「传输文件」'
    Write-Host '  3. 开发者选项里的「USB 调试」已打开'
    Write-Host '  4. 手机上已点「允许 USB 调试」'
    Write-Host ''
    Read-Host '按回车关闭'
    exit 1
}

$changed = 0
foreach ($d in $found) {
    Write-Host ''
    Write-Host ("设备: " + $d.FriendlyName) -ForegroundColor White
    Write-Host ("  " + $d.InstanceId) -ForegroundColor DarkGray

    $parts = $d.InstanceId -split '\\'
    if ($parts.Count -ne 3) {
        Write-Host '  实例 ID 格式异常，跳过' -ForegroundColor DarkGray
        continue
    }

    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\USB\$($parts[1])\$($parts[2])\Device Parameters"
    if (-not (Test-Path $regPath)) {
        Write-Host '  找不到 Device Parameters，跳过' -ForegroundColor DarkGray
        continue
    }

    $existing = (Get-ItemProperty -Path $regPath -Name 'DeviceInterfaceGUIDs' -ErrorAction SilentlyContinue).DeviceInterfaceGUIDs
    if ($existing -contains $AdbGuid) {
        Write-Host '  GUID 已存在，只需重启接口' -ForegroundColor Gray
    } else {
        try {
            New-ItemProperty -Path $regPath -Name 'DeviceInterfaceGUIDs' -PropertyType MultiString -Value $AdbGuid -Force | Out-Null
            Write-Host '  已写入 adb 接口 GUID' -ForegroundColor Green
            $changed++
        } catch {
            Write-Host ('  写入失败: ' + $_.Exception.Message) -ForegroundColor Red
            continue
        }
    }

    Write-Host '  正在重启 USB 接口...' -ForegroundColor Gray
    $null = pnputil /restart-device "$($d.InstanceId)" 2>&1
    Start-Sleep -Milliseconds 1200
}

# 重启 adb 服务
$adb = $null
$baseDir = Split-Path -Parent $PSScriptRoot
foreach ($p in @(
    (Join-Path $baseDir 'platform-tools\adb.exe'),
    (Join-Path $PSScriptRoot 'platform-tools\adb.exe')
)) {
    if (Test-Path $p) { $adb = $p; break }
}
if (-not $adb) {
    $c = Get-Command adb -ErrorAction SilentlyContinue
    if ($c) { $adb = $c.Source }
}

Write-Host ''
if ($adb) {
    & $adb kill-server 2>&1 | Out-Null
    Start-Sleep -Milliseconds 600
    Write-Host '当前设备列表：' -ForegroundColor Cyan
    & $adb devices -l
} else {
    Write-Host '(没找到 adb.exe)' -ForegroundColor DarkGray
}

Write-Host ''
if ($changed -gt 0) {
    Write-Host '修复完成。请关闭并重新打开「手机相册传输」。' -ForegroundColor Green
} else {
    Write-Host '没有需要修改的项。如果程序还是看不到手机，请检查上面的设备列表。' -ForegroundColor Yellow
}
Write-Host ''
Read-Host '按回车关闭'
