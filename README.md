# OrclSKBK

**Oracooll Surface Keyboard Backlight Keeper** - current version **1.3.001**

A tiny Windows tray app that stops the Surface keyboard backlight from switching itself off after
about 30 seconds without typing. Touch the trackpad once, and the light stays on for as long as you
want.

**Made by Claude, prompted by Oracooll.** All code was written by Claude (Anthropic's AI model);
Oracooll prompted, tested on real hardware and directed the work. MIT licensed.

Built and verified on a **Surface Laptop Studio 2** running **Windows 11 25H2**. It should work on
any Surface whose keyboard exposes the standard HID Keyboard Backlight interface that Windows 11
25H2 (build 26200.7922, February 2026) introduced. Reports from other models are welcome in the
issues.

## Install

1. Download `OrclSKBK.exe` or the latest zip from [Releases](https://github.com/Oracooll/OrclSKBK/releases)
   and unpack it.
2. Either just run `OrclSKBK.exe`, or run `install.ps1` (right-click,
   *Run with PowerShell*) to copy it to `%LOCALAPPDATA%\OrclSKBK`, start
   it at sign-in and add a Start menu shortcut. `uninstall.ps1` reverses that. It deletes only
   what the app creates, keeps folders that contain anything else, and lists whatever it could
   not remove.
3. A small keyboard icon appears in the tray: yellow = active, grey = paused or no device.

The binary is not code-signed, so Windows SmartScreen warns on first run. Choose *More info* →
*Run anyway*, or don't trust the download at all and build it yourself: `build.ps1` compiles the
single source file with the C# compiler that ships in every Windows installation. No Visual Studio
or .NET SDK required. The release notes carry the SHA-256 of each zip.

## Upgrading from 1.2.0 or earlier

Versions up to 1.2.0 were called *Surface Keyboard Backlight Keeper*. OrclSKBK copies their
settings on first start. Running install.ps1 also stops the old version and removes its startup
entry, Start menu shortcut and installed exe. uninstall.ps1 removes both names.

## Privacy, permissions and files

It needs no admin rights and no driver, makes no network connections, and does not read
keystrokes: it opens only the HID *Keyboard Backlight* collection, never the keyboard's input
collection. Everything it writes is per-user:

| Where | What | When |
| --- | --- | --- |
| `HKCU\Software\OrclSKBK` | Settings | When you change a menu option, and on first run |
| `HKCU\...\CurrentVersion\Run`, value `OrclSKBK` | Start at sign-in | Only when *Start with Windows* is ticked or `install.ps1` runs |
| `%LOCALAPPDATA%\OrclSKBK\keeper.log` | Log: start-up, device path and IDs, brightness sent, errors | Only while *Write log file* is ticked |
| same file | Crash report (exception details) | Always, if the app crashes |

The log rotates at about 512 KB and keeps one previous file (`keeper.old.log`). The app reads the
brightness Windows stores under `HKLM\SOFTWARE\Microsoft\Lighting\Backlight\State` but never
writes to `HKLM`.

**Scope:** it acts on every keyboard that exposes the standard HID Keyboard Backlight interface,
not only Surface keyboards. Each keyboard follows its own brightness as stored by Windows, and a
fixed level from the menu applies to all of them. With today's hardware that is, in practice,
the built-in Surface keyboard.

## Using it

* **Click** the tray icon for the menu: on/off, brightness (follow Windows or a fixed level),
  refresh interval, keep-alive method, pause rules, *Start with Windows*, log file, About.
* **Double-click** the icon to toggle it on and off.
* **Touch the trackpad or press a key** once when the light is off; the app keeps it on from there.
* `OrclSKBK.exe --test` steps the light through its levels (run it while the
  light is on) to prove the app can drive the backlight on your model.

## Why the backlight turns off, and why nothing in Settings fixes it

* The keyboard on Surface laptops hangs off the Surface System Aggregator Module (SAM), the
  embedded controller. Its firmware turns the backlight off after roughly 30 s with no *physical*
  key press. Microsoft's answer in every forum thread is "file a suggestion in Feedback Hub".
* Since Windows 11 25H2 build 26200.7922, Windows drives keyboard backlights through a standard
  HID *Keyboard Backlight* collection (usage page `0x0C`, usage `0x07`). The Surface keyboard
  exposes it as collection `Col05` of the SAM keyboard (VID 045E, PID 0C73), with four presets
  (0, 3, 6, 12 nits). Windows sends a *Set Level* output report (usage `0x7B`) whenever you press
  the backlight key and stores the level under `HKLM\SOFTWARE\Microsoft\Lighting\Backlight\State`.
* The Windows component that owns this (`Windows.Internal.Devices.Lights.BacklightServer.dll`)
  contains no idle-timeout logic, which confirms the timeout lives in the keyboard firmware.

## What the app does

Every few seconds (default 10 s) it re-sends the same Set Level report that Windows itself sends,
using the brightness Windows last stored. That re-arms the firmware's idle timer, so the light
stays on.

* **Keeps it on, cannot switch it on.** Once the firmware has switched the light off, no HID
  write relights it (tested: same level, different presets, explicit 0 → 12, feature reports,
  bursts, keyboard LED reports, injected mouse moves). Only a physical key press, a trackpad touch
  or the display turning back on does. Hence: touch the trackpad once.
* **Follows the keyboard's backlight key.** Press it to change brightness; the app picks up the
  new value within one refresh interval. If you press the key until the light is *off* while the
  app is running, it stays off until you press the key again. Enabling the app always means "on":
  if Windows currently stores "off" it uses the last brightness it saw (6 nits by default).
* **Pauses** when the display is off, when the session is locked, and optionally on battery.
* **Refresh interval** 5, 10 or 15 s. The firmware timeout was observed to be under 25 s, so
  longer intervals are not offered.
* **Two keep-alive methods.** The default simply repeats the current level (no flicker). If the
  light still times out on your model, switch to *Tiny dip and restore on every refresh*, which
  writes one step lower and immediately back (6 → 5 → 6 nits within 20 ms).
* **Reports are built from the keyboard's own descriptor.** The Windows HID parser
  (`HidP_SetUsageValue`, `HidP_GetUsageValue`, `HidP_GetUsageValueArray`) encodes and decodes
  every report from the device's descriptor, so report IDs (including none), field positions and
  sizes are not assumed. Writes go through `WriteFile`, because the Surface HID mini-driver does not
  implement `HidD_SetOutputReport` (error 50, `ERROR_NOT_SUPPORTED`); that call remains only as a
  fallback for other keyboards. Short writes count as failures.
* **Device work runs on a background thread.** A slow or stuck driver cannot freeze the tray
  menu or delay lock and display handling. If a device call hangs for more than 3 s, the app
  cancels it, shows "Keyboard not responding" and re-detects the keyboard.
* **Several keyboards are tracked separately**: brightness changes, the "turned off with its key"
  state and failure counts are kept per keyboard.

## Versions

Versions follow the Oracooll 1.X.XXX scheme and are set in one place, `AppInfo` at the top of the source. Releases are titled "OrclSKBK 1.X.XXX" and tagged `v1.X.XXX`.

## Files

| Path | What |
| --- | --- |
| `src\OrclSKBK.cs` | Complete source. C# 5 on purpose so the in-box compiler builds it. |
| `src\app.manifest` | Runs as invoker, per-monitor DPI aware. |
| `build.ps1` | Builds `build\OrclSKBK.exe` with `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`. |
| `install.ps1`, `uninstall.ps1` | Per-user install to Local AppData with start-at-sign-in, and removal. |
| `release.ps1` | Builds, zips, hashes and publishes a GitHub release (maintainer use). |
| `.github/workflows/build.yml` | CI build on every push. |
| `CHANGELOG.md` | What changed in each version. |

## Caveats

* Verified on the Surface Laptop Studio 2 only. Other models, several keyboards at once, and
  lock, resume and firmware-update behaviour on other hardware need checking by their owners. The
  keyboard has no readable "light is on" state, so the app cannot tell whether the light is
  currently on; it just keeps re-arming the timer.
* The display off → on transition relights the keyboard (the embedded controller is told about
  display state by the Surface driver), but a screen blink was judged not worth the complication:
  on Modern Standby machines Windows suspends desktop apps the moment the screen goes off, so the
  app would need execution-required and display-required power requests to pull it off. A trackpad
  touch does the same job with no side effects.
* Keeping the LEDs on costs a little battery; *Pause when running on battery* exists for that.
* If *Change keyboard brightness automatically when lighting changes* is enabled in Windows, the
  stored manual level may lag behind what Windows applied. Pick a fixed level in that case.

## Credits and licence

Made by Claude, prompted by Oracooll. MIT License, see [LICENSE](LICENSE).

## Sources consulted

* Microsoft Learn: [Keyboard Backlight Implementation Guide](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/keyboard-backlight-implementation-guide)
* Microsoft Q&A: [How do I keep the keyboard backlight always on the Surface Laptop Studio](https://learn.microsoft.com/en-us/answers/questions/2311128/how-do-i-keep-the-keyboard-backlight-always-on-the),
  [Surface Laptop 7th Edition keyboard backlight](https://learn.microsoft.com/en-us/answers/questions/2335373/microsoft-surface-laptop-7th-edition-keyboard-back),
  [How to permanently keep the keyboard backlight on](https://learn.microsoft.com/en-us/answers/questions/5882513/how-to-permanently-keep-the-keyboard-backlight-on),
  [Surface Keyboard Backlighting](https://learn.microsoft.com/en-us/answers/questions/136fd266-9ab1-46e7-8f81-6dc5d435a540/surface-keyboard-backlighting?forum=surface-all)
* Surface Forums: [Permanent Keyboard light?](https://www.surfaceforums.net/threads/permanent-keyboard-light.15968/)
* AutoHotkey forum: [Is there a script to auto press a key to prevent backlight timeout?](https://www.autohotkey.com/boards/viewtopic.php?t=37362)
* linux-surface: [Surface Aggregator Module controller notifications](https://github.com/torvalds/linux/blob/master/drivers/platform/surface/aggregator/controller.c),
  [Surface HID driver](https://github.com/torvalds/linux/blob/master/drivers/hid/surface-hid/surface_hid.c)
