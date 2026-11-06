# TBMini

**English** | [简体中文](README.zh-CN.md)

**A tiny tray app that replaces the Lenovo 联想百应 (Baiying) / Vantage UI on the ThinkBook 16p G6.**
Switch the performance mode (including Geek mode), the GPU mode (hybrid / iGPU only / dGPU direct), the battery charge mode, keyboard backlight, Fn lock, color presets, Dolby profile and more, and see live CPU and GPU temperature and power plus total power, all from one small panel without launching Lenovo's apps. It can also adjust itself: Saver on battery, Performance on USB-C, Geek on the full-power adapter, plus automatic iGPU / Hybrid switching (see [Auto adjust](#auto-adjust)).
Lenovo's background services and drivers must stay installed: TBMini only replaces their UI.

<p align="center">
  <img src="docs/screenshot-en-dark.png" width="720" alt="TBMini panel (dark)"><br>
  <img src="docs/screenshot-en-light.png" width="720" alt="TBMini panel (light)">
</p>

## Features

| Section | Settings |
|---|---|
| Status bar | One row: CPU temperature and power, dGPU temperature and power, total power, power source and battery level (plus the charge rate while charging, e.g. "AC · 82% · +45 W"), current GPU mode. Readings refresh every 1.5 s, only while the panel is open |
| Performance mode | Saver / Smart / Performance / Geek, or **Auto adjust** (see [Auto adjust](#auto-adjust)). Geek only with the full-power barrel adapter (the "300 W adapter" in TBMini's notices): not on battery and not on any USB-C PD charger, which Lenovo's firmware reports as a separate adapter type. The Geek tile is grayed out (its tooltip does not show while it is disabled). The tray menu item is grayed out with the reason in the menu's own text, "(needs the full-power adapter)" or "(needs AC power)", and the panel's status line explains it when the charger is the cause. If the laptop is in Geek and a USB-C charger (or one that can't keep up) takes over, TBMini leaves Geek itself, even with auto adjust off (to Performance; with auto adjust on, the Auto adjust table applies). Fn+Q still works and the icon follows it. With auto adjust on, Fn+Q or Lenovo's own mode switch does not turn auto adjust off, so auto adjust may change the mode again at the next power change. TBMini also keeps the mode steady across plug and unplug (see [Windows power mode sync](#windows-power-mode-sync)) |
| GPU mode | Hybrid / iGPU only (switches within Hybrid, no restart; has no effect while an app is using the dGPU: TBMini names the apps using it, from nvidia-smi, and says the dGPU detaches once they close) / dGPU direct (switching to or from dGPU direct needs a restart; TBMini asks). A change waiting for a restart shows in the header GPU chip as "Current → Pending", with a "Restart now" link in the panel's status line |
| Battery | Charge mode (Normal / Rapid / Conserve), night charging |
| Power | Always-on USB (Off / On / + battery), power on when the lid opens |
| Keyboard | Backlight (Off / Low / High / Auto), backlight off after 30 s, remember backlight, top-row keys (hotkeys / F1-F12), Fn/Ctrl swap, Caps Lock / Num Lock indicators, touchpad on/off |
| Display | Factory X-Rite color presets for the internal display (D65 / Native / sRGB / P3-D65 / P3-DCI / Adobe RGB / REC709), re-applied automatically after a GPU mode switch; refresh rate (60 / 120 / 165 Hz, or Dynamic: Windows 11's dynamic refresh rate, 60 Hz while idle and up to 165 Hz while scrolling or animating) |
| Sound | Dolby Audio (Dynamic / Movie / Music / Game / Voice / Custom / Off), AI mic noise suppression (Off / Group / Single / Spatial) |
| App | Language (System / 中文 / English), start at sign-in |

Settings the machine does not support are hidden. BIOS-backed settings are also hidden when TBMini is not running as administrator, and so is Start at sign-in; the tray menu then shows "Restart TBMini as administrator (needed for GPU switching)" in its place. The GPU mode tiles stay visible, and clicking one offers to restart TBMini as administrator.

### Auto adjust

Auto adjust drives both the performance mode and the GPU mode. Pick the **Auto adjust** tile in the panel (or in the tray menu) to turn it on; while it is on its tile is selected and the mode it applied has a colored ring.

| Power source | Performance mode | GPU action |
|---|---|---|
| Battery | Saver | undoes any switch it made |
| Charger that can't keep up | Saver | GPU handled by its USB-C class (counts as 65 W or less when otherwise unknown) |
| 65 W-class USB-C charger | Performance | Hybrid → iGPU only |
| 100 W USB-C charger | Performance | iGPU only → Hybrid |
| Full-power adapter | Geek | iGPU only → Hybrid |

- **When it acts:** only when the power source changes or Windows reports the charger can't keep up (no background polling). Picking a performance mode yourself turns it off, and so does clicking the Auto adjust tile or menu item again. Turning auto adjust off stops the automatic performance-mode and GPU switching, and TBMini forgets any override it would have undone; the USB-C notices still appear, and Geek still drops to Performance on a USB-C charger or one that can't keep up.
- **Can't keep up:** Windows reports the supply as inadequate and the battery drains while plugged in (e.g. 45 W, or a 65/100 W charger under heavy load), or Lenovo reports the charger as not supported (too weak). The Windows report is held until you unplug, the laptop wakes from sleep, the full-power adapter is detected, or you turn auto adjust on again.
- **dGPU on USB-C power:** Lenovo's firmware caps the dGPU on USB-C chargers (about 15 W on 65 W or less, slower than the iGPU; about 30 W on 100 W). When you connect a USB-C charger (65 W class, 100 W, or one whose class is unknown) TBMini says so, in Hybrid (or Lenovo's Auto GPU mode) and in dGPU direct, whether or not auto adjust is on. The full-power adapter gets a notice only when auto adjust switched the GPU or failed to. When auto adjust moves iGPU only to Hybrid on a 100 W or full-power charger, TBMini says "Switched to Hybrid; back to iGPU only when you unplug." Undoing a switch shows "GPU switched back to X".
- **Charger class:** TBMini reads the CPU power limit PL2 the EC sets for the charger (needs administrator): PL2 of 55-90 W means 100 W, and PL2 of 50 W or less outside Saver means 65 W or less. If that doesn't decide it, it uses the dGPU's own power cap, but only if the dGPU is already awake. If that fails too, a charger Windows flags as not keeping up counts as 65 W. When the class stays unknown, nothing is switched and only a general USB-C notice is shown.
- **Undoing:** each switch is undone when its reason goes away (battery, or the other kind of charger), back to what you had (Lenovo's Auto GPU mode is treated like Hybrid and restored to Auto). Overrides left from earlier are also undone at startup and after wake, if their reason has gone. A GPU mode you pick yourself is never reverted (connecting a charger still applies the rules above). An override is also dropped when Lenovo's own software (or anything else) changes the GPU mode, not only when you pick one in TBMini.
- **Limits:** if a switch fails (an app is holding the dGPU) the notice says so and TBMini retries quietly every 30 s, up to 10 times, and says "GPU switched to X" once it works. While the internal display is off (lid closed), any automatic GPU switch, whether starting an override for a charger connected while the lid was closed or undoing one, is made once the display is back; a connect's switch then shows only "GPU switched to X", without repeating the charger notice. dGPU direct is never switched automatically, since that needs a restart; it only gets a notice. Automatic switching uses only the live Hybrid / iGPU only switch: it never writes the BIOS GraphicsDevice setting or restarts. While a BIOS GPU change is waiting for a restart it does nothing: no failure notice and no retries.
- **Administrator:** only the GPU switching and the CPU-limit check for the charger class need TBMini running as administrator (the default with Start at sign-in). Auto adjust of the performance mode works without it, through Lenovo's service.

### Windows power mode sync

Windows keeps a separate "Power mode" for plugged in and on battery, and Lenovo's dispatcher follows it, so a plug or unplug used to change the Lenovo mode by itself. The sync is always on (there is no setting). TBMini copies the power mode stored for the current source to the other source at startup and about 1 s after every performance mode change, so the mode stays the same across plug and unplug. This changes a Windows setting: the two per-source power modes become equal and stay that way after uninstalling TBMini. It needs Windows 11 24H2 or later and does nothing on older builds.

### Temperature and power

- **CPU power:** the Windows "Energy Meter" performance counter (Intel RAPL package power). No driver needed.
- **CPU temperature:** the CPU's own package sensor, read through the optional [PawnIO](https://pawnio.eu) driver (the same driver LibreHardwareMonitor and FanControl use). Install it with `winget install namazso.PawnIO`; TBMini must run as administrator, as it does when started at sign-in. Without PawnIO the CPU bubble shows power only. The ThinkBook 16p G6's firmware gives no other way to read it: Lenovo's `GetCPUTemp` WMI call returns "invalid object" and there are no ACPI thermal zones.
- **dGPU temperature and power:** NVML from the NVIDIA driver. When the dGPU is sleeping in Hybrid mode the bubble shows "Asleep", and TBMini never wakes the dGPU just to read it. In iGPU-only mode it shows "Off".
- **Total power:** on battery this is the battery discharge rate, which is the real whole-system power. On AC the ThinkBook 16p G6 has no whole-system power reading at all: there is no ACPI power meter, the battery reports 0 W, and the CPU's platform energy counter (PSys) is not wired up. So on AC the bubble is relabeled "CPU+GPU" and shows CPU plus dGPU power. On laptops that do wire up PSys, TBMini shows it as whole-system power when PawnIO is installed.
- **Charge rate:** while the battery charges, the power chip adds the power going into the battery (e.g. "AC · 82% · +45 W"). That is only the battery's share of the adapter's output, not the laptop's total draw. When the battery is full or held at the Conserve limit it reads 0 W and is not shown.
- Readings are taken only while the panel is open. When the panel closes, polling stops and every handle is released, so there is no background power cost. One reading costs about 0.5 ms.

## Install

1. Download `TBMini-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/FrostFlops/TBmini/releases) and unzip it anywhere. You can check the download against `SHA256SUMS.txt`.
2. Run `TBMini.exe` and accept the UAC prompt (GPU mode and BIOS settings need administrator rights). The exe is not code-signed, so SmartScreen may warn on first launch; choose "Run anyway".
3. Turn on **Start at sign-in** under App in the panel (or right-click the tray icon → Start at sign-in). TBMini copies itself to `C:\Program Files\TBMini` and starts at sign-in as a scheduled task: no UAC prompt, and it also starts on battery.
4. Optional: install [PawnIO](https://pawnio.eu) (`winget install namazso.PawnIO`) to see the CPU temperature. See [Temperature and power](#temperature-and-power).

Requires Windows 10/11 x64 and .NET Framework 4.8 (included in Windows 11). Some features need more:

- **Administrator rights:** GPU switching (manual and automatic), the BIOS settings, Start at sign-in and the CPU temperature.
- **Windows 11 24H2 or later:** the touchpad switch and the power mode sync.
- **Windows 11:** dynamic refresh rate. The glass look needs Windows 11 22H2.

### Update

Right-click the tray icon → Exit, then run the new `TBMini.exe`. If Start at sign-in is on, the new version replaces the copy in `C:\Program Files\TBMini` by itself.

### Uninstall

Turn off Start at sign-in → right-click the tray icon → Exit. Then delete the `C:\Program Files\TBMini` folder and the registry key `HKEY_CURRENT_USER\Software\TBMini`.

## Usage

- **Left-click** the tray icon to open the panel. **Right-click** for the menu: the four performance modes (Geek grayed out with the reason) and "Auto adjust (now: X)", the three GPU modes, "Restart to switch to: X" when a GPU change is waiting for a restart, language, exit and so on. Without administrator rights the menu shows "Restart TBMini as administrator (needed for GPU switching)" in place of Start at sign-in.
- Click the **TBMini** title in the panel header to open the project page on GitHub. When TBMini runs elevated (the default), the page is handed to Explorer so it opens in your normal, unelevated browser.
- The panel slides in briefly and the controls animate with springs: toggles, the selection pill of segmented choices, tiles that squish when pressed, and the selected color flowing from the old tile into the new one with a fading tail. All of it snaps instantly when "Animation effects" is off in Windows settings.
- On Windows 11 22H2 and later, the panel and all its menus (the tray menu and submenus, and the panel's own dropdowns such as Color mode and Dolby Audio) are glass: Windows' own acrylic material (a live blur of whatever is behind) under translucent cards. With "Transparency effects" off in Windows settings, or before Windows 11 22H2, they are drawn solid in the same colors. The panel and menus follow Windows' light / dark app mode.
- The tray icon's color and symbol show the current performance mode.
- **Language:** App → Language in the panel, or Language in the tray menu. The change applies immediately. "System" follows the Windows display language: Chinese on Chinese Windows, English otherwise.
- Before the touchpad can be turned off, a USB or Bluetooth mouse-class device must be connected (some keyboards and receivers with mouse functions count). Once it is off, unplugging any mouse that was connected at that moment turns the touchpad back on. While TBMini is running, **Ctrl+Alt+Shift+T** also turns it back on.

### Command line

TBMini is a GUI app: in PowerShell, use `TBMini --status | Out-String` or `--out status.txt` to get the full output. Command-line output uses the same language setting.

```
TBMini --status [--out file]          current state (read-only)
TBMini --features [--out file]        every setting's current value (read-only)
TBMini --sensors [--out file]         temperature and power readings (read-only)
TBMini --power saver|auto|perf|geek   switch the performance mode (auto = Smart, not auto adjust;
                                      geek needs the full-power adapter; this doesn't turn off the
                                      tray's auto adjust, which may change the mode at the next power change)
TBMini --autostart on|off|status      start at sign-in (on/off need an admin terminal)
TBMini --version
TBMini --help                         list the commands (also -h or /?)
```

## Dependencies and compatibility

TBMini ships no Lenovo or NVIDIA files. It calls components that are already installed, so **do not uninstall the Lenovo Baiying / Lenovo Vantage services**; you just no longer need to open them:

- Performance mode: `LenovoProcessManagement` (the Lenovo Dispatcher service, installed with the drivers)
- Battery, keyboard backlight, Fn lock, always-on USB: the Lenovo ACPI driver (`\\.\EnergyDrv`)
- dGPU direct (needs a restart), power on with lid open: the BIOS WMI interface (`Lenovo_SetBiosSetting`); Fn/Ctrl swap: `LENOVO_BIOS_ASSISTANT`
- Hybrid / iGPU only switch, manual or automatic: `LENOVO_GAMEZONE_DATA` (`SetIGPUModeStatus`)
- Charger type: the adapter type from `\\.\EnergyDrv` plus Windows' power supply status. USB-C charger class: the CPU power limit PL2 (read-only, needs administrator) from `LENOVO_OTHER_METHOD` (`GetFeatureValue`), and NVML's dGPU power limit
- Power mode sync: powrprof's `PowerSetUserConfiguredACPowerMode` / `PowerSetUserConfiguredDCPowerMode` (Windows 11 24H2 or later)
- dGPU temperature and power: the NVIDIA driver's `nvml.dll`; CPU power: the Energy Meter counters built into Windows; battery discharge: the Windows battery driver
- CPU temperature (optional): the [PawnIO](https://pawnio.eu) driver, if you install it. TBMini embeds PawnIO's official signed `IntelMSR` module, which reads a fixed list of CPU registers and can write a few tuning ones (power limits, OC/VR mailbox, prefetcher control); TBMini only calls its read function (LGPL-2.1, see [third_party/PawnIO](third_party/PawnIO)), and loads PawnIO's library only after checking its signature
- Caps Lock / Num Lock indicators: the `LenovoFnAndFunctionKeys` service
- Refresh rate: Windows display settings (`ChangeDisplaySettingsEx` for fixed rates; the Windows 11 `DISPLAYCONFIG_PATH_BOOST_REFRESH_RATE` path flag for Dynamic). No Lenovo component needed
- Color presets: the factory X-Rite profiles (`C:\Windows\System32\spool\drivers\color\TPLCD_*.icm`). They have no effect while HDR or "Automatically manage color for apps" is on
- Dolby Audio: the Dolby audio driver and service, plus `DolbyRpcClient.dll` from Lenovo Baiying (copied to the install folder when Start at sign-in is turned on)
- Mic noise suppression: Lenovo Vantage's noise-suppression plugin. Before loading it, TBMini checks its signature with WinVerifyTrust and confirms that Lenovo signed it

Note that uninstalling Lenovo Baiying or Vantage makes Lenovo reset Geek mode, battery conservation and night charging to their defaults.

Tested only on the **ThinkBook 16p G6 IAX**. GPU detection and readings support Intel iGPU + NVIDIA dGPU machines only. TBMini does not check the model: other ThinkBook / IdeaPad / Xiaoxin models that use the same Lenovo interfaces may partly work (unsupported settings are hidden). Use it on other models at your own risk.

## Safety

- Switching to or from **dGPU direct** (the GPU mux setting) and **power on with lid open** are written to the BIOS through `Lenovo_SetBiosSetting`; Hybrid / iGPU only goes through `LENOVO_GAMEZONE_DATA` live. For these BIOS writes, TBMini refuses when a BIOS supervisor password is set (change the setting in BIOS setup instead), checks the target value against the BIOS's list of choices when one is provided, tries to discard the change if saving fails, and reads the value back afterwards. **Fn/Ctrl swap** is written to the firmware through `LENOVO_BIOS_ASSISTANT` and read back afterwards.
- The GPU mode actually in effect comes from the hardware (which GPU drives the internal display), so Fast Startup and hibernation do not mislead it. While the internal display is off (lid closed with an external monitor, or "Second screen only"), the current mode cannot be confirmed and GPU switching is refused; turn the internal display on first.
- When switching to or from dGPU direct (any GPU change that needs a BIOS change and a restart) with BitLocker on for the system drive, TBMini reminds you to make sure you have the recovery key. Before changing the other BIOS settings (power on with lid open, Fn/Ctrl swap), check this yourself.
- No polling while idle: performance mode changes arrive through registry notifications, and everything else is driven by system events. Temperature and power are read only while the panel is open, and the panel closes itself when you click elsewhere, lock the PC, the screen turns off or the PC goes to sleep. Apart from animation frames, the only timers are short re-checks that run just when needed: a few seconds after a charger or power event (auto adjust, the USB-C check), 1 s after a performance mode change (power mode sync), 3 s after a display change, resume or unlock (re-applies the color preset), a quiet retry every 30 s (up to 10 times) after a failed automatic GPU switch, the panel's 1.5 s sensor refresh while it is open, and, only while the touchpad is turned off, a check 1.5 s after a device change and a 15 s external-mouse check.

This project is not affiliated with or endorsed by Lenovo or NVIDIA. ThinkBook, Lenovo Vantage and 联想百应 are trademarks of Lenovo; NVIDIA is a trademark of NVIDIA Corporation; Dolby is a trademark of Dolby Laboratories. Changing hardware and BIOS settings carries risk; you use this software at your own risk.

## Build from source

Requires the .NET SDK 8 or later (the target framework is .NET Framework 4.8):

```
dotnet build TBMini.csproj -c Release
```

The output is `bin/Release/TBMini.exe`. Pushing a `vX.Y.Z` tag makes GitHub Actions build and publish a release; a `vX.Y.Z-suffix` tag is published as a pre-release.

## Credits

Interface semantics were cross-checked against [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit), its community fork [LenovoLegionToolkit-Team/LenovoLegionToolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit), and [ThinkBook Toolkit](https://github.com/lhzlhz419/ThinkBookToolkit). The CPU temperature uses [PawnIO](https://pawnio.eu) and its [IntelMSR module](https://github.com/namazso/PawnIO.Modules) by namazso.

## License

[GPL-3.0](LICENSE)
