# Changelog

## 1.2.0 (unreleased)

Addresses the findings of an independent code audit of v1.1.0 (4 October 2026). No change in
behaviour on the Surface Laptop Studio 2: the bytes sent to the keyboard are identical.

* **Reports come from the descriptor.** Set Level, level suggestions and the initial level are
  encoded and decoded with the Windows HID parser and the device's preparsed data, instead of
  assuming the value sits in byte 1. Report ID 0 is supported, `HidP_GetValueCaps` status and report
  counts are checked, and a short `WriteFile` counts as a failure.
* **No blocking calls on the UI thread.** All HID I/O moved to one background thread. A watchdog
  cancels a device call that hangs for more than 3 s (`CancelSynchronousIo`) and re-detects the
  keyboard, so the tray menu, lock and display handling, and Exit always respond.
* **Per-keyboard state.** "Turned off with its key", last brightness and failure counts are kept
  per device, so several keyboards can't confuse each other, and one failing keyboard always
  triggers re-detection.
* **Lock state at start-up.** The session's lock state is read from Windows when the app starts
  and re-checked on every refresh, so starting in a locked session no longer writes until the next
  lock event. A failed display-notification registration is now logged.
* **Logging setting means what it says.** With *Write log file* off, nothing is written except
  crash reports, and crash reports go through the same size-bounded rotation.
* **Safer scripts.** `install.ps1` and `uninstall.ps1` stop only processes whose exe carries this
  app's product name. `uninstall.ps1` deletes only the files the app creates, keeps folders that
  contain anything else, verifies the result, lists anything left and exits with 1 in that case.
  `release.ps1` stops only a copy running from the build folder.
* Self test shows the encoded report bytes for each level.

## 1.1.0 (18 September 2026)

First public release.
