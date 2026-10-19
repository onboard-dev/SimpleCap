@echo off
rem ==========================================================
rem  SimpleCap build script
rem  Uses csc.exe bundled with Windows (.NET Framework 4.x).
rem  No SDK / no external libraries required.
rem ==========================================================
setlocal
rem This script lives in bat\. Work in the project root (src\, app.ico, SimpleCap.exe).
cd /d "%~dp0.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Please enable .NET Framework 4.x.
  pause
  exit /b 1
)

set ICON=
if exist app.ico set ICON=/win32icon:app.ico

echo Building SimpleCap.exe ...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /utf8output ^
  /out:SimpleCap.exe %ICON% ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  src\*.cs

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  pause
  exit /b 1
)
echo.
echo [OK] SimpleCap.exe was created.
echo      Captures are saved to: %USERPROFILE%\Pictures\SimpleCap
rem  Close automatically after 2 seconds (any key closes immediately)
if /i not "%1"=="nopause" timeout /t 2 >nul
endlocal
