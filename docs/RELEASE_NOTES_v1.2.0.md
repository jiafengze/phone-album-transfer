## 下载即用，不用再解压

之前发布的是一个 zip，得下完、解压、再找到 exe 双击——多两步。现在改成**单文件**：

**下载 `PhoneAlbum-v1.2.0-win64.exe` → 双击 → 直接用。**

不需要解压，不需要安装，不产生任何快捷方式或注册表项。

### 怎么做到的

adb（8.1 MB）和两个 dll、NOTICE 文件用 **GZip 压缩后嵌入 exe 资源**，首次运行时自动释放到 `%LOCALAPPDATA%\PhoneAlbum\platform-tools\`。

| | 旧（zip） | 新（单文件） |
|---|---|---|
| 下载 | zip 4.24 MB | **exe 4.27 MB** |
| 需要解压 | 是 | **否** |
| 需要安装 | 否 | 否 |
| 首次启动 | 立即 | 慢 1–2 秒（释放 adb） |
| 之后启动 | 立即 | 立即 |

体积几乎没变，因为压缩率和 zip 差不多。

### 「修复连接」按钮

原来驱动修复工具是 zip 里的一个 `.bat` 文件。改成单文件后，它也被嵌进了 exe，并做成了工具栏上的一个按钮：

- 手机连不上时，点工具栏最右侧的「**修复连接**」
- 程序会弹出 UAC 提权确认，然后自动修 Windows 通用 WinUSB 驱动缺失的 adb 接口 GUID
- 修完可以直接重新读取手机，不用重启程序

### 其他

- 新增 `PHONEALBUM_DATA` 环境变量，可自定义数据目录（便携部署用）
- 修复：重复定义的工具栏控件

## 已验证

- 从「干净环境」模拟下载运行：自动释放 adb.exe（8,079.60 KB）、两个 dll、NOTICE.txt、版本标记、fix-adb.ps1
- 释放出的 adb 版本 37.0.1，`adb devices` 能正常识别设备
- 程序界面正常加载 3936 个媒体文件，工具栏「修复连接」按钮就位

## 环境要求

- Windows 10 1903+ / Windows 11
- .NET Framework 4.8（系统自带）
- 手机端建议开 USB 调试（不开也能用 MTP 模式）

详见 [README](https://github.com/jiafengze/phone-album-transfer#readme)。
