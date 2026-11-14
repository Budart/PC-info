@echo off
REM Compila o PCInfo.exe no proprio PC usando o compilador que ja vem no Windows (.NET Framework 4)
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /win32icon:PCInfo.ico /out:PCInfo.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll PCInfo.cs
if exist PCInfo.exe (start "" PCInfo.exe) else (pause)
