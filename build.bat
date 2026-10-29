@echo off
rem Builds VolumeBoost.exe with the C# compiler that ships with Windows (.NET Framework 4)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set ICON=
if exist "%~dp0src\speaker.ico" set ICON=/win32icon:"%~dp0src\speaker.ico"
"%CSC%" /nologo /target:winexe /optimize %ICON% /out:"%~dp0VolumeBoost.exe" "%~dp0src\VolumeBoost.cs"
