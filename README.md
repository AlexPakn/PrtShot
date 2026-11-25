# PrtShot

PrtShot is a minimalist screenshot tool for Windows 10 and 11. It stays in the system tray. Press **Print Screen**, select a region, and save it as a JPEG.

## Usage

1. Run `PrtShot.exe`.
2. Press **Print Screen** and drag with the left mouse button to select a region.
3. Press **Enter** to save, **Ctrl+C** to save and copy the image, or **Esc** or the right mouse button to cancel.

Screenshots go to the `Screenshots` subfolder of your Windows Pictures folder. The tray menu lets you start a capture, open that folder, enable startup at sign-in, or exit.

## Fullscreen and elevated apps

The selection overlay is designed to appear without taking keyboard focus from the active app. If a fullscreen game briefly covers the overlay, press **Print Screen** again to bring it forward.

Games and other apps running as administrator may block PrtShot unless it also runs as administrator.

If **Print Screen** opens Snipping Tool, turn off its Print Screen shortcut in Windows keyboard settings.

## Build from source

On 64-bit Windows, use the following build command in Command Prompt from the repository folder.

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /r:System.Drawing.dll /r:System.Windows.Forms.dll /win32manifest:PrtShot.manifest /win32icon:PrtShot.ico /out:PrtShot.exe PrtShot.cs DxgiCapture.cs
```

PrtShot uses Windows Forms and Win32 APIs. For black captures, it falls back to DXGI Desktop Duplication.
