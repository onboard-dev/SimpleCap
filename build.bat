@echo off
rem Shortcut to bat\build.bat (the real build script). SimpleCap.exe is created here in the root.
call "%~dp0bat\build.bat" %*
exit /b %errorlevel%
