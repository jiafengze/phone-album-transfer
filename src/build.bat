@echo off
rem ============================================================
rem  PhoneAlbum build script
rem  Recompiles PhoneAlbum.cs into PhoneAlbum.exe using the
rem  in-box .NET Framework C# compiler (no SDK required).
rem ============================================================

set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found at %CSC%
    pause
    exit /b 1
)

cd /d "%~dp0"

if exist PhoneAlbum.exe del /f PhoneAlbum.exe

"%CSC%" /nologo /target:winexe /optimize+ /win32manifest:app.manifest ^
  /out:PhoneAlbum.exe ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  PhoneAlbum.cs

if exist PhoneAlbum.exe (
    echo.
    echo [OK] Build succeeded: PhoneAlbum.exe
) else (
    echo.
    echo [FAILED] Build failed.
)
echo.
pause
