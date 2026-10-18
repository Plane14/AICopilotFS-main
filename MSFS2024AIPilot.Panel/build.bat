@echo off
REM Builds the in-sim AI Pilot toolbar panel and installs it into the MSFS community folder.
REM
REM fspackagetool.exe from the MSFS SDK is not a self-contained packager: it launches the
REM packaging pass from inside an installed FlightSimulator2024.exe and waits for it. It
REM therefore needs the game itself on this machine.
setlocal enabledelayedexpansion

set "PANEL_DIR=%~dp0"
set "SDK_DIR=%MSFS2024_SDK%"
if not defined SDK_DIR set "SDK_DIR=%MSFS_SDK%"
if defined SDK_DIR if "%SDK_DIR:~-1%"=="\" set "SDK_DIR=%SDK_DIR:~0,-1%"
if not defined SDK_DIR if exist "%PANEL_DIR%..\MSFS 2024 SDK\Tools\bin\fspackagetool.exe" set "SDK_DIR=%PANEL_DIR%..\MSFS 2024 SDK"
set "PACKAGE_TOOL=%SDK_DIR%\Tools\bin\fspackagetool.exe"

if not exist "%PACKAGE_TOOL%" (
  REM Microsoft Store installs keep a SimConnect SDK copy here; the packaging tool is
  REM only in the standalone SDK, so fall back to the Store lookup as a hint.
  echo ERROR: fspackagetool.exe was not found.
  echo.
  echo Set the MSFS2024_SDK or MSFS_SDK environment variable to your MSFS 2024 SDK
  echo folder, or put the SDK in "%PANEL_DIR%..\MSFS 2024 SDK".
  exit /b 1
)

REM --- Pre-flight: the tool needs the installed game -------------------------------
set "FS_EXE="

REM 1. An explicit override next to the tool wins.
if exist "%SDK_DIR%\Tools\bin\fspackagetool_overrideExePath.txt" (
  set /p FS_EXE=<"%SDK_DIR%\Tools\bin\fspackagetool_overrideExePath.txt"
)

REM 2. The Microsoft Store package.
if not defined FS_EXE (
  set "FS_EXE=%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\FlightSimulator2024.exe"
)

REM 3. Common non-store locations.
if not exist "%FS_EXE%" (
  for %%D in (
    "C:\Program Files\Microsoft Flight Simulator 2024"
    "C:\Program Files (x86)\Microsoft Flight Simulator 2024"
    "C:\Program Files (x86)\Steam\steamapps\common\Microsoft Flight Simulator 2024"
  ) do (
    if exist "%%~D\FlightSimulator2024.exe" set "FS_EXE=%%~D\FlightSimulator2024.exe"
  )
)

if not exist "%FS_EXE%" (
  echo ERROR: Microsoft Flight Simulator 2024 does not appear to be installed.
  echo.
  echo fspackagetool.exe cannot run in a standalone mode: it starts the packaging pass
  echo from the installed game and waits for it to finish.
  echo.
  echo Either install MSFS 2024 on this machine, or write the path to
  echo FlightSimulator2024.exe into:
  echo   %SDK_DIR%\Tools\bin\fspackagetool_overrideExePath.txt
  echo.
  echo The panel sources in PackageSources\ are unchanged and can be inspected without
  echo the tool; see AGENTS.md for what still needs a live simulator to verify.
  exit /b 1
)

echo Using MSFS SDK:     %SDK_DIR%
echo Using MSFS 2024 at: %FS_EXE%

REM --- Build ----------------------------------------------------------------------
pushd "%PANEL_DIR%"
"%PACKAGE_TOOL%" package.xml -nopause
if errorlevel 1 (
  echo.
  echo Package build failed. On a first run the tool builds its base packages before
  echo compiling - run this script again to retry.
  popd
  exit /b 1
)
popd

if not exist "%PANEL_DIR%Packages\msfs2024-ai-pilot" (
  echo ERROR: the tool reported success but no package was produced.
  echo Expected: %PANEL_DIR%Packages\msfs2024-ai-pilot
  exit /b 1
)

REM --- Install into the community folder ------------------------------------------
set "PACKAGES_ROOT="
for %%F in (
  "%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt"
  "%APPDATA%\Microsoft Flight Simulator 2024\UserCfg.opt"
) do (
  if not defined PACKAGES_ROOT if exist "%%~F" call :read_installed_packages_path "%%~F"
)

if not defined PACKAGES_ROOT (
  set "PACKAGES_ROOT=%USERPROFILE%\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages"
)

set "COMMUNITY=%PACKAGES_ROOT%\Community"
if exist "%PACKAGES_ROOT%\Community2024" set "COMMUNITY=%PACKAGES_ROOT%\Community2024"

if not exist "%COMMUNITY%" (
  echo ERROR: Community folder not found: %COMMUNITY%
  echo The package was built anyway. Copy "%PANEL_DIR%Packages\msfs2024-ai-pilot" into
  echo your community folder by hand.
  exit /b 1
)

if exist "%COMMUNITY%\msfs2024-ai-pilot" rmdir /s /q "%COMMUNITY%\msfs2024-ai-pilot"
xcopy /e /y /i "%PANEL_DIR%Packages\msfs2024-ai-pilot" "%COMMUNITY%\msfs2024-ai-pilot"
if errorlevel 1 (
  echo Failed to copy the package into the community folder.
  exit /b 1
)

echo.
echo Installed to: %COMMUNITY%\msfs2024-ai-pilot
echo Enable the panel in MSFS under Options - General - Navigation - Instrument Panel Layout.
endlocal
goto :eof

:read_installed_packages_path
for /f "tokens=1,* delims= " %%A in ('findstr /b /c:"InstalledPackagesPath" "%~1"') do set "PACKAGES_ROOT=%%~B"
if defined PACKAGES_ROOT set "PACKAGES_ROOT=!PACKAGES_ROOT:"=!"
goto :eof
