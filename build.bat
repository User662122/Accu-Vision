@echo off
REM ============================================================
REM  AccuVisionRepost - one-click build (no Visual Studio needed)
REM  Uses MSBuild if present, otherwise the C# compiler that ships
REM  inside Windows itself (Microsoft.NET Framework v4 csc.exe).
REM  The exe is built 32-bit (x86) because Jet OLEDB is 32-bit only.
REM ============================================================
setlocal
cd /d "%~dp0"
set SRC=src\AccuVisionRepost
set OUT=dist
if not exist "%OUT%" mkdir "%OUT%"

where msbuild.exe >nul 2>nul
if %ERRORLEVEL%==0 (
  echo Building with MSBuild...
  msbuild "%SRC%\AccuVisionRepost.csproj" /t:Rebuild /p:Configuration=Release /p:Platform=x86
  if %ERRORLEVEL%==0 (
    copy /Y "%SRC%\bin\x86\Release\AccuVisionRepost.exe" "%OUT%\AccuVisionRepost.exe" >nul
    copy /Y "%SRC%\bin\x86\Release\AccuVisionRepost.exe.config" "%OUT%\AccuVisionRepost.exe.config" >nul 2>nul
    goto done
  )
  echo MSBuild failed, trying in-box csc.exe ...
)

set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo ERROR: csc.exe not found at %CSC%
  echo Install .NET Framework 4.8 Developer Pack or Visual Studio Build Tools, then run this file again.
  if /I not "%~1"=="--nopause" if not defined CI pause
  exit /b 1
)

echo Building with %CSC% ...
"%CSC%" /nologo /target:exe /platform:x86 /optimize+ ^
  /out:"%OUT%\AccuVisionRepost.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Data.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll ^
  "%SRC%\Program.cs" ^
  "%SRC%\Properties\AssemblyInfo.cs" ^
  "%SRC%\Core\JetDb.cs" ^
  "%SRC%\Core\CompanyDb.cs" ^
  "%SRC%\Core\Voucher.cs" ^
  "%SRC%\Core\Helpers.cs" ^
  "%SRC%\Core\CloneEngine.cs" ^
  "%SRC%\Core\AccNewGuards.cs" ^
  "%SRC%\Core\HeadlessSpec.cs" ^
  "%SRC%\Core\HeadlessEngine.cs" ^
  "%SRC%\Core\HeadlessCli.cs" ^
  "%SRC%\Core\SelfTest.cs" ^
  "%SRC%\Ui\MainForm.cs" ^
  "%SRC%\Ui\LookupDialog.cs"

if not %ERRORLEVEL%==0 (
  echo.
  echo BUILD FAILED - see the errors above.
  if /I not "%~1"=="--nopause" if not defined CI pause
  exit /b 1
)
copy /Y "%SRC%\App.config" "%OUT%\AccuVisionRepost.exe.config" >nul

:done
echo.
echo BUILD OK: %OUT%\AccuVisionRepost.exe
echo.
echo Optional check of the posting engine (creates a scratch test database in %%TEMP%%):
echo   "%OUT%\AccuVisionRepost.exe" /selftest %%TEMP%%
echo It must print: SELFTEST PASSED
echo.
if /I not "%~1"=="--nopause" if not defined CI pause
