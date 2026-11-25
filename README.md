# PrtShot

PrtShot is a minimalist screenshot tool for Windows 10 and 11. It stays in the system tray. Press **Print Screen**, select a region, and save it as a JPEG.

## Usage

1. Run `PrtShot.exe`.
2. Press **Print Screen** and drag with the left mouse button to select a region.
3. Press **Enter** to save, **Ctrl+C** to save and copy the image, or **Esc** or the right mouse button to cancel.

Screenshots go to the `Screenshots` subfolder of your Windows Pictures folder. The tray menu lets you start a capture, open that folder, enable startup at sign-in, or exit.

## Fullscreen and elevated apps

The selection overlay is designed to appear without taking keyboard focus from the active app. If a fullscreen game briefly covers the overlay, press **Print Screen** again to bring it forward.

Games and other apps running as administrator can interfere with the **Print Screen** shortcut. In testing with **Identity V on Windows**, running PrtShot as administrator restored the shortcut.

If **Print Screen** opens Snipping Tool, turn off its Print Screen shortcut in Windows keyboard settings.

## Build from source

On 64-bit Windows, use the following build command in Command Prompt from the repository folder.

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /r:System.Drawing.dll /r:System.Windows.Forms.dll /win32manifest:PrtShot.manifest /win32icon:PrtShot.ico /out:PrtShot.exe PrtShot.cs DxgiCapture.cs
```

PrtShot uses Windows Forms and Win32 APIs. For black captures, it falls back to DXGI Desktop Duplication.

## Optional elevated startup

If an app running as administrator blocks the capture shortcut, try running PrtShot as administrator for that session. For regular use with elevated apps, you can enable elevated startup through Windows Task Scheduler.

1. Copy a trusted build of `PrtShot.exe` into `C:\Program Files\PrtShot`. The folder should allow standard users to read and run the file, with write access reserved for administrators and Windows.
2. If PrtShot already starts at sign-in through its tray menu, turn that startup option off to avoid launching two instances.
3. Open **Task Scheduler** as administrator and choose **Create Task**. In **General**, name the task `PrtShot`, select your Windows account, and enable **Run only when user is logged on** and **Run with highest privileges**.
4. In **Triggers**, add **At log on** for that same account.
5. In **Actions**, add **Start a program**. For a standard Windows installation, use `C:\Windows\System32\cmd.exe` as **Program/script**, `C:\Program Files\PrtShot` as **Start in**, and the following as **Add arguments (optional)**. Adjust the absolute paths if your installation uses different locations.

```text
/D /S /C "set "COR_ENABLE_PROFILING=0" & set "COMPLUS_ProfAPI_ProfilerCompatibilitySetting=" & set "APPDOMAIN_MANAGER_ASM=" & set "APPDOMAIN_MANAGER_TYPE=" & set "DEVPATH=" & set "PATH=C:\Windows\System32;C:\Windows" & start "" /B "C:\Program Files\PrtShot\PrtShot.exe""
```

If you already created the task, replace its arguments in **Properties** → **Actions** → **Edit**. The command window may appear briefly and closes after launching PrtShot.

In **Settings**, clear **Stop the task if it runs longer than** so PrtShot can stay in the tray. On a laptop, check the battery options in **Conditions** if you want it to start and stay running on battery.

Save the task. Close any running PrtShot instance, then select the task in **Task Scheduler Library** and click **Run** to test it. Use **Disable** in the same view to turn off elevated startup. Future updates can reuse this task as long as the executable stays at the same path.

**Security trade-off.** A scheduled task with highest privileges starts PrtShot with administrator rights at every sign-in. A flaw in the app or a tampered executable could then have system-wide impact. Keep the executable, its folder, and the task writable only by trusted administrators or Windows. The command above sets a system-only `PATH` for the child process and disables selected .NET runtime overrides. These steps reduce startup risks, while PrtShot still runs with administrator rights. Keep UAC enabled.

See [Microsoft’s Task Scheduler security documentation](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks) for details.
