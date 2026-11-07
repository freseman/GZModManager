# GZ Mod Manager

A small mod manager for **Godzilla: Destroy All Monsters Melee Remastered** (Steam), modeled on Mod Organizer 2. Mods are kept in their own folder, and the game folder is only touched when you click Deploy.

Download the release build on Nexus Mods:
https://www.nexusmods.com/godzilladestroyallmonstersmeleeremastered/mods/5

## Building from source

No Visual Studio or SDK needed. It builds with the C# compiler that ships with Windows (.NET Framework 4.x).

1. Download or clone this repository.
2. Double-click `build.bat` (or run it from a command prompt in this folder).
3. `GZModManager.exe` is created in the same folder.

`build.bat` runs:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /out:GZModManager.exe /win32icon:GZModManager.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Core.cs MainForm.cs
```

## Files

- `Core.cs` - game detection (Steam library lookup), mod install/import, deploy and UE4SS toggling
- `MainForm.cs` - the Windows Forms user interface
- `GZModManager.ico` - application icon

## What it touches

- Its own folder (where you extract it), for stored mods and settings
- The game's `GZPro\Content\Paks\~mods\` and `GZPro\Binaries\Win64\ue4ss\` folders when you Deploy or toggle UE4SS

It makes no network connections itself. The "Download UE4SS" menu item and prompt only open the UE4SS GitHub releases page in your default browser.
