# FlickMeter

FlickMeter 是一款适用于 Windows 10/11 x64 的轻量游戏性能浮窗。当前发布为 **v1.0.0**，下载 ZIP 并完整解压后即可运行。

## 下载

在 GitHub 发布页的 **Assets** 区域下载 Windows 运行包 **FlickMeter-1.0.0-win-x64.zip**。项目适用于 Windows 10/11 x64。[发布页](https://github.com/Byte-prod/FlickMeter/releases/tag/v1.0.0) · [直接下载](https://github.com/Byte-prod/FlickMeter/releases/download/v1.0.0/FlickMeter-1.0.0-win-x64.zip)

完整解压后，三个文件应在同一目录。

```text
FlickMeter/
├── FlickMeter.exe
├── PresentMon.exe
└── PresentMon-LICENSE.txt
```

## 功能

- 显示实时 **FPS 和 1% Low**。各项读数可在「显示数据」中单独开关。
- 监控 **CPU 和 GPU** 的占用率、温度、功耗（W）及频率（MHz），也可查看物理内存占用率、已用内存与专用显存占用（GiB）。双显卡电脑优先显示当前游戏使用的 GPU。
- 浮窗支持**横版、竖版、透明度和鼠标穿透**。大小从 130% 到 265% 共 10 档，每档加 15 个百分点。各项读数可单独开关。
- 在「固定位置」中拖动 **0–100 滑杆**或输入数值来定位。横向 50、纵向 0 是顶部居中，增减数据时浮窗仍以屏幕中线对齐。选择「解除固定（可拖动）」即可手动拖动。
- 文字颜色支持预设、自定义和从 **18 种颜色**中抽选的随机配色。托盘菜单可设置开机自启动或创建桌面快捷方式。

## 使用方法

1. 打开解压出的 `FlickMeter` 文件夹，双击 `FlickMeter.exe`。游戏帧率采集可能需要 Windows 管理员权限。
2. 启动游戏并将游戏窗口切到前台。建议使用窗口化或无边框全屏模式，方便显示浮窗。
3. 右键点击系统托盘中的 FlickMeter 图标，或在关闭鼠标穿透时右键点击浮窗。在「显示数据」中可单独开关指标，也可通过菜单调整布局和开机自启动。开启或关闭鼠标穿透时菜单保持打开，点击菜单外可关闭。

首次采样可能需要片刻，1% Low 需要积累一段时间的帧数据。游戏或硬件未提供某项读数时，该项显示为 `—`。

## 反馈

遇到问题时请提供 Windows 版本、显卡型号、驱动版本、游戏名称及窗口模式。[提交 Issue](https://github.com/Byte-prod/FlickMeter/issues/new)

## 源码与构建

仓库的 [source](source/) 目录包含与 v1.0.0 发布包对应的 C# 源码和构建脚本。也可在发布页下载 GitHub 生成的源码压缩包。[v1.0.0 发布页](https://github.com/Byte-prod/FlickMeter/releases/tag/v1.0.0)

在 Windows PowerShell 中打开仓库根目录，运行以下命令。脚本使用系统的 64 位 .NET Framework C# 编译器。

```powershell
.\source\Build.ps1
```

默认输出为 `source\build\FlickMeter.exe`。运行自行编译的程序时，把 Intel PresentMon 2.6.0 x64 版命名为 `PresentMon.exe`，放到主程序所在目录。也可使用发布运行包中的同名文件。

## 第三方组件

帧率采集使用 Intel [PresentMon 2.6.0](https://github.com/GameTechDev/PresentMon/tree/v2.6.0)。PresentMon 的 [MIT 许可文本](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/LICENSE.txt) 随程序打包。
