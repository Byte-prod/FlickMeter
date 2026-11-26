# FlickMeter

FlickMeter 是一款适用于 Windows 10/11 x64 的轻量游戏性能浮窗。下载单个 EXE 即可运行。

## 下载

[下载最新版 FlickMeter.exe](https://github.com/Byte-prod/FlickMeter/releases/latest/download/FlickMeter.exe) · [v1.1.1 更新说明](https://github.com/Byte-prod/FlickMeter/releases/tag/v1.1.1) · [查看版本记录](https://github.com/Byte-prod/FlickMeter/releases)

## 功能

- 显示实时 **FPS、1% Low 和 PC 平均延时**。PC 平均延时单独显示在浮窗末尾，标为「延迟」，单位为 ms，取游戏提供的近期有效样本的平均值。
- 监控 **CPU 和 GPU** 的占用率、温度、功耗（W）及频率（MHz），也可查看物理内存占用率、已用内存与专用显存占用（GiB）。双显卡电脑优先显示当前游戏使用的 GPU。
- 浮窗支持**横版、竖版、透明度和鼠标穿透**。大小从 130% 到 265% 共 10 档，每档加 15 个百分点。各项读数可单独开关。
- 在「固定位置」中拖动 **0–100 滑杆**或输入数值来定位。横向 50、纵向 0 是顶部居中，增减数据时浮窗仍以屏幕中线对齐。选择「解除固定（可拖动）」即可手动拖动。
- 文字颜色支持预设、自定义和从 **18 种颜色**中抽选的随机配色。托盘菜单可设置开机自启动或创建桌面快捷方式。

## 使用方法

1. 运行 `FlickMeter.exe`。如果 Windows 请求管理员授权，允许后即可采集游戏帧率。
2. 启动游戏并将游戏窗口切到前台。建议使用窗口化或无边框全屏模式，方便显示浮窗。
3. 右键点击系统托盘中的 FlickMeter 图标，或在关闭鼠标穿透时右键点击浮窗。在「显示数据」中可单独开关指标，也可通过菜单调整布局和开机自启动。

首次采样可能需要片刻，1% Low 需要积累一段时间的帧数据。PC 平均延时取决于游戏是否提供相应的延时数据，仅反映电脑处理游戏输入到送出画面的时间。游戏或硬件未提供某项读数时，该项显示为 `—`。

## 反馈

遇到问题时请提供 Windows 版本、显卡型号、驱动版本、游戏名称及窗口模式。[提交 Issue](https://github.com/Byte-prod/FlickMeter/issues/new)

## 第三方组件

帧率采集使用 Intel [PresentMon 2.6.0](https://github.com/GameTechDev/PresentMon/tree/v2.6.0)。PresentMon 的 [MIT 许可文本](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/LICENSE.txt) 随程序打包。
