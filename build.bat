@echo off
rem Build DrType.exe with the C# compiler that ships with Windows (.NET Framework 4.x). Nothing to install.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required - it is built into Windows 10/11.
  pause
  exit /b 1
)
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:dist\DrType.exe /win32icon:DrType.ico /win32manifest:app.manifest /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Accessibility.dll src\*.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)
echo.
echo Done: dist\DrType.exe
pause
