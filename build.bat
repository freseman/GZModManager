@echo off
rem Rebuild GZModManager.exe with the C# compiler that ships with Windows (.NET Framework 4).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:GZModManager.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Core.cs MainForm.cs
pause
