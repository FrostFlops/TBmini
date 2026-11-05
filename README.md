# TBMini

**A tiny tray app that replaces the Lenovo 联想百应 / Vantage UI on the ThinkBook 16p G6.**
Switch performance mode (incl. Geek mode), GPU mode (hybrid / iGPU-only / dGPU-direct), battery charge mode, keyboard backlight, Fn lock, colour presets, Dolby profile and more — from one small panel, without launching Lenovo's apps.
Lenovo's background services and drivers must stay installed: TBMini only replaces their UI.

一个极简的托盘工具，用来替代 ThinkBook 16p G6 上「联想百应」的界面。不用再打开联想那一堆软件，就能切换性能模式、显卡模式和常用的硬件设置。

<p align="center">
  <img src="docs/screenshot-dark.png" width="720" alt="TBMini panel (dark)"><br>
  <img src="docs/screenshot-light.png" width="720" alt="TBMini panel (light)">
</p>

## 功能

| 分类 | 设置 |
|---|---|
| 性能模式 | 省电 / 智能 / 性能 / 极客（极客仅接电源时可用；Fn+Q 照常可用，图标会同步） |
| 显卡模式 | 混合 / 集显（在混合模式内切换，不用重启；独显正被程序占用时不生效）/ 独显直连（切入或切出独显直连都需要重启，工具会提示） |
| 电池 | 充电模式（普通 / 快充 / 养护）、夜间充电 |
| 电源 | 持续 USB 供电（关 / 开 / 含电池关机）、开盖开机 |
| 键盘 | 键盘背光（关 / 暗 / 亮 / 自动）、背光 30 秒自动关、记住背光设置、F1-F12 首行键（热键 / F1-F12）、Fn/Ctrl 交换、大写/数字锁定提示、触摸板开关 |
| 显示 | 内屏出厂 X-Rite 色彩预设（D65 / Native / sRGB / P3-D65 / P3-DCI / Adobe RGB / REC709）。切换显卡模式后会自动重新应用 |
| 声音 | 杜比音效（动态 / 电影 / 音乐 / 游戏 / 语音 / 定制 / 关闭）、麦克风 AI 降噪（关 / 多人 / 单人 / 空间） |

机器不支持的设置项会自动隐藏。没有以管理员身份运行时，BIOS 相关的设置项也会隐藏。

## 安装

1. 从 [Releases](https://github.com/FrostFlops/TBmini/releases) 下载 `TBMini-vX.Y.Z-win-x64.zip`，解压到任意位置。附带的 `SHA256SUMS.txt` 可以用来校验下载。
2. 运行 `TBMini.exe`，在 UAC 提示里允许（显卡模式和 BIOS 项需要管理员权限）。程序没有代码签名，第一次运行时 SmartScreen 可能会拦截，选「仍要运行」即可。
3. 右键托盘图标 → **开机自启**。程序会复制到 `C:\Program Files\TBMini`，并以计划任务方式在登录时启动（不再弹 UAC，电池供电时也会启动）。

需要 Windows 10/11 x64 和 .NET Framework 4.8（Windows 11 自带）。触摸板开关需要 Windows 11 24H2 或更高版本。

### 更新

右键托盘图标 → 退出，然后运行新版本的 `TBMini.exe`。如果开着「开机自启」，新版本会自动覆盖 `C:\Program Files\TBMini` 里的旧版本。

### 卸载

右键托盘图标 → 关闭「开机自启」→ 退出，然后删除 `C:\Program Files\TBMini` 文件夹和注册表项 `HKEY_CURRENT_USER\Software\TBMini`。

## 使用

- **左键**托盘图标：打开面板。**右键**：菜单（开机自启、退出等）。
- 托盘图标的颜色和图案表示当前性能模式。
- 关闭触摸板前必须接有 USB 或蓝牙鼠标类设备（部分带鼠标功能的键盘或接收器也算）。关掉后，只要当时连接的任一鼠标被拔掉，触摸板会自动重新打开。TBMini 运行时，按 **Ctrl+Alt+Shift+T** 也可以重新打开触摸板。

### 命令行

TBMini 是图形界面程序：在 PowerShell 里可以用 `TBMini --status | Out-String` 或 `--out status.txt` 拿到完整输出。

```
TBMini --status [--out file]          当前状态（只读）
TBMini --features [--out file]        所有设置项的当前值（只读）
TBMini --power saver|auto|perf|geek   切换性能模式
TBMini --autostart on|off|status      开机自启（on/off 需要在管理员终端里运行）
TBMini --version
```

## 依赖与兼容性

TBMini 本身不包含任何联想的文件，它调用的是机器上已经安装的联想组件，所以**请不要卸载联想百应 / Lenovo Vantage 服务**，只是不用再打开它们：

- 性能模式：`LenovoProcessManagement`（Lenovo Dispatcher 服务，随驱动安装）
- 电池、键盘背光、Fn Lock、持续 USB 供电：联想 ACPI 驱动（`\\.\EnergyDrv`）
- 显卡模式、开盖开机：BIOS 的 WMI 接口（`Lenovo_SetBiosSetting`）；Fn/Ctrl 交换：`LENOVO_BIOS_ASSISTANT`
- 大写/数字锁定提示：`LenovoFnAndFunctionKeys` 服务
- 色彩模式：出厂预装的 X-Rite 校色文件（`C:\Windows\System32\spool\drivers\color\TPLCD_*.icm`）。开着 HDR 或「自动管理应用颜色」时预设不生效
- 杜比音效：Dolby 音频驱动/服务，以及联想百应自带的 `DolbyRpcClient.dll`（开启「开机自启」时会复制一份到安装目录）
- 麦克风降噪：Lenovo Vantage 的降噪插件（加载前会用 WinVerifyTrust 校验文件签名，并确认签名者是联想）

另外，卸载联想百应 / Vantage 时，联想会把极客模式、电池养护、夜间充电恢复成默认值。

目前只在 **ThinkBook 16p G6 IAX** 上测试过。显卡检测只支持 Intel 核显 + NVIDIA 独显的机型。程序不做机型检查：其他使用同一套联想接口的 ThinkBook / IdeaPad / 小新可能部分可用（不支持的设置项会隐藏），在其它机型上使用风险自负。

## 安全说明

- **显卡模式**和**开盖开机**通过 `Lenovo_SetBiosSetting` 写入 BIOS：BIOS 设有管理员密码时会直接拒绝（请进 BIOS 修改）；BIOS 提供可选值列表时会校验目标值；保存失败会尝试撤销；写入后会读回校验。**Fn/Ctrl 交换**通过 `LENOVO_BIOS_ASSISTANT` 写入固件，写入后读回校验。
- 当前实际生效的显卡模式以硬件为准（内屏挂在哪块显卡上），不会被快速启动 / 休眠误导。内屏没点亮时（合盖外接显示器、仅第二屏幕模式）无法确认当前模式，显卡切换会被拒绝，请先打开内屏。
- 切换独显直连时，如果检测到系统盘开着 BitLocker，工具会提醒你先确认手里有恢复密钥。修改其它 BIOS 项（开盖开机、Fn/Ctrl 交换）前，请自行确认。
- 空闲时不轮询：性能模式的变化通过注册表通知获得，其它都由系统事件驱动。只有在触摸板被关闭期间，才会每 15 秒检查一次外接鼠标是否还在。

本项目与联想（Lenovo）无关，也未获得联想的认可。ThinkBook、Lenovo Vantage、联想百应是联想的商标；Dolby 是杜比实验室的商标。修改硬件和 BIOS 设置有风险，使用本软件即表示你自行承担风险。

## 从源码构建

需要 .NET SDK 8 或更高版本（目标框架是 .NET Framework 4.8）：

```
dotnet build TBMini.csproj -c Release
```

输出在 `bin/Release/TBMini.exe`。推送 `vX.Y.Z` 标签会由 GitHub Actions 自动构建并发布 Release；`vX.Y.Z-xxx` 形式的标签会发布为预发布版本。

## 致谢

接口语义参考了 [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit)、它的社区维护分支 [LenovoLegionToolkit-Team/LenovoLegionToolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit)，以及 [ThinkBook Toolkit](https://github.com/lhzlhz419/ThinkBookToolkit)。

## 许可证

[GPL-3.0](LICENSE)
