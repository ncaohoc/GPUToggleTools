@echo off
rem Builds GpuToggle.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Uses forward slashes only (backslashes can get stripped when downloading).
cd /d "%~dp0"
set "CSC=%SystemRoot%/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found: %CSC%
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:GpuToggle.exe /win32manifest:app.manifest /win32icon:app.ico /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll GpuToggle.cs
if errorlevel 1 (
  echo.
  echo Build FAILED
  pause
  exit /b 1
)
echo.
echo Built GpuToggle.exe
pause