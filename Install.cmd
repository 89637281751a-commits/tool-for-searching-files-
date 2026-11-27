@echo off
setlocal
set "APPDIR=%LOCALAPPDATA%\QuickFind"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto fail
if not exist "%APPDIR%" mkdir "%APPDIR%"
set "MODE=/setup"
if exist "%APPDIR%\QuickFind.exe" set "MODE="
set "STAGED=%APPDIR%\QuickFind-update-%RANDOM%-%RANDOM%.exe"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:"%~dp0QuickFind.ico" /out:"%STAGED%" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll "%~dp0QuickFind.cs"
if errorlevel 1 goto fail
move /y "%STAGED%" "%APPDIR%\QuickFind.exe" >nul
if errorlevel 1 goto fail
start "" "%APPDIR%\QuickFind.exe" %MODE%
exit /b 0
:fail
if defined STAGED if exist "%STAGED%" del "%STAGED%"
echo Setup failed. Close QuickFind before reinstalling. Windows .NET Framework 4.x is required.
pause
exit /b 1
