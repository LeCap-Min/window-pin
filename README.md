# 窗口置顶 · Window Pin

一个轻量 Windows 桌面工具：选择应用窗口，设置或取消置顶。中文界面，免安装，无需 Python。

## 运行

从 **[最新 Release](https://github.com/LeCap-Min/window-pin/releases/latest)** 下载 **[WindowPin.exe](https://github.com/LeCap-Min/window-pin/releases/latest/download/WindowPin.exe)**，双击运行。支持 Windows 10/11，依赖 .NET Framework 4.8。

Release 同时提供 `SHA256SUMS.txt` 用于校验下载文件。仓库保留源码；自行构建会在本地生成 `窗口置顶.exe`。

1. 在列表中选择目标窗口，也可以按窗口标题或程序名称搜索。
2. 点击 **置顶所选**；点击 **取消置顶** 或双击条目可切换状态。
3. 可分别置顶多个窗口，并将工具收起到系统托盘。

全局快捷键为 **Ctrl + Alt + T**；被占用时自动改用 **Ctrl + Alt + F8**，以界面显示为准。退出工具会恢复本次修改过的窗口原状态。

详细操作和适用范围见 [使用说明](./使用说明.md)。置顶适用于普通桌面窗口，不保证覆盖独占全屏或系统安全界面。

## 构建与验证

在项目目录打开 PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建并运行独立测试窗口、界面和 EXE 启动检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

使用 Windows 的 .NET Framework C# 编译器，不需要下载第三方依赖。测试需要交互式 Windows 桌面环境；测试结果生成在 `tests/results/`，不会纳入版本控制。验证边界见 [测试说明](./测试说明.md)。

## 隐私与权限

应用在本机读取窗口标题、程序名称和进程 ID，用于显示和选择目标窗口；这些内容只在内存与本地界面中使用。应用不联网、不记录窗口清单、不设置开机启动、不修改注册表，也不注入其他进程。

程序默认以普通用户权限运行。若目标以管理员身份运行且操作失败，可退出本工具后通过右键菜单选择“以管理员身份运行”。

## 文件结构

- `src/`：窗口管理、界面、快捷键与托盘源码。
- `assets/`：应用图标。
- `tests/`：跨进程窗口与界面测试源码。
- `build.ps1`：单文件 EXE 构建脚本。
