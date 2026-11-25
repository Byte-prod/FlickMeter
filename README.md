# FlickMeter

FlickMeter 是一款适用于 Windows 10/11 x64 的轻量游戏性能浮窗。下载单个 EXE 即可运行。

## 下载

[下载最新版 FlickMeter.exe](https://github.com/Byte-prod/FlickMeter/releases/latest/download/FlickMeter.exe) · [V1.1.0 更新说明](https://github.com/Byte-prod/FlickMeter/releases/tag/v1.1.0) · [查看版本记录](https://github.com/Byte-prod/FlickMeter/releases)

## 功能

- 实时显示 FPS、1% Low、CPU 与 GPU 占用率和温度，以及内存占用率。
- 显示 CPU 与 GPU 功耗（W）、已用物理内存和专用显存占用（GiB）。
- 双显卡电脑会优先显示当前游戏使用的 GPU。GPU 占用率与温度采集已在英伟达和 AMD 显卡上得到验证。
- 数值变化时保持浮窗宽度稳定。支持横版、竖版、130%–265% 缩放、透明度和鼠标穿透。关闭鼠标穿透时，固定位置的浮窗也可以右键打开菜单。
- 可在托盘菜单中开启开机自启动，程序会在登录 Windows 后打开。设置完成后，托盘会提示操作结果。

## 使用方法

1. 运行 `FlickMeter.exe`。如果 Windows 请求管理员授权，允许后即可采集游戏帧率。
2. 启动游戏并将游戏窗口切到前台。建议使用窗口化或无边框全屏模式，方便显示浮窗。
3. 右键点击系统托盘中的 FlickMeter 图标，或在关闭鼠标穿透时右键点击浮窗。在「显示数据」中可单独开关指标，也可通过菜单调整布局和开机自启动。

首次采样可能需要片刻。1% Low 需要积累一段时间的帧数据才会出现。部分设备和驱动无法提供温度、功耗或显存读数，对应指标显示为 `—`。

## 反馈

遇到问题时请提供 Windows 版本、显卡型号、驱动版本、游戏名称及窗口模式。[提交 Issue](https://github.com/Byte-prod/FlickMeter/issues/new)

## 第三方组件

帧率采集使用 Intel [PresentMon 2.6.0](https://github.com/GameTechDev/PresentMon/tree/v2.6.0)。PresentMon 的 [MIT 许可文本](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/LICENSE.txt) 随程序打包。
