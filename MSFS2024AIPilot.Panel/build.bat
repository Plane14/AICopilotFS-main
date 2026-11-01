@echo off
REM Builds the in-sim AI Pilot toolbar panel and installs it into the MSFS community folder.
REM
REM fspackagetool.exe from the MSFS SDK is not a self-contained packager: it launches the
REM packaging pass from inside an installed FlightSimulator2024.exe and waits for it. It
REM therefore needs the game itself on this machine.
setlocal enabledelayedexpansion

set "PANEL_DIR=%~dp0"
set "SDK_DIR=%MSFS_SDK%"
if "%SDK_DIR%"=="" set "SDK_DIR=%PANEL_DIR%..\MSFS 2024 SDK"
set "PACKAGE_TOOL=%SDK_DIR%\Tools\bin\fspackagetool.exe"

if not exist "%PACKAGE_TOOL%" (
  REM Microsoft Store installs keep a SimConnect SDK copy here; the packaging tool is
  REM only in the standalone SDK, so fall back to the Store lookup as a hint.
  echo ERROR: fspackagetool.exe was not found.
  echo.
  echo Set the MSFS_SDK environment variable to your MSFS 2024 SDK folder, or put the
  echo SDK in "%PANEL_DIR%..\MSFS 2024 SDK".
  exit /b 1
)

REM --- Read configuration from simulator-paths.json ---------------------------------
set "CONFIG_FILE=%PANEL_DIR%..\..\MSFS2024AIPilot.Host\simulator-paths.json"
set "MSFS_EXE_CONFIG="
set "MSFS_SDK_CONFIG="
set "COMMUNITY_FOLDER_CONFIG="
set "COMMUNITY2024_FOLDER_CONFIG="

if exist "%CONFIG_FILE%" (
  REM Use PowerShell to parse JSON config
  for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command ^
    "$config = Get-Content '%CONFIG_FILE%' -Raw | ConvertFrom-Json; ^
     if ($config.msfsExecutablePath) { Write-Output \"MSFS_EXE=$config.msfsExecutablePath\" }; ^
     if ($config.msfsSdkPath) { Write-Output \"MSFS_SDK=$config.msfsSdkPath\" }; ^
     if ($config.communityFolder) { Write-Output \"COMMUNITY_FOLDER=$config.communityFolder\" }; ^
     if ($config.community2024Folder) { Write-Output \"COMMUNITY2024_FOLDER=$config.community2024Folder\" }"`) do (
    set "%%A"
  )
)

REM --- Pre-flight: the tool needs the installed game -------------------------------
set "FS_EXE="

REM 1. Config file override for MSFS executable
if defined MSFS_EXE_CONFIG (
  set "FS_EXE=%MSFS_EXE_CONFIG%"
)

REM 2. An explicit override next to the tool wins.
if not defined FS_EXE if exist "%SDK_DIR%\Tools\bin\fspackagetool_overrideExePath.txt" (
  set /p FS_EXE=<"%SDK_DIR%\Tools\bin\fspackagetool_overrideExePath.txt"
)

REM 3. Config file override for MSFS SDK
if defined MSFS_SDK_CONFIG (
  set "SDK_DIR=%MSFS_SDK_CONFIG%"
  set "PACKAGE_TOOL=%SDK_DIR%\Tools\bin\fspackagetool.exe"
)

REM 4. The Microsoft Store package.
if not defined FS_EXE (
  set "FS_EXE=%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\FlightSimulator2024.exe"
)

REM 5. Common non-store locations.
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
  echo Or set msfsExecutablePath in simulator-paths.json.
  echo.
  echo The panel sources in PackageSources\ are unchanged and can be inspected without
  echo the tool; see AGENTS.md for what still needs a live simulator to verify.
  exit /b 1
)

echo Using MSFS SDK:     %SDK_DIR%
echo Using MSFS 2024 at: %FS_EXE%

REM --- Build ----------------------------------------------------------------------
pushd "%PANEL_DIR%"
"%PACKAGE_TOOL%" package.xml
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
set "COMMUNITY="

REM 1. Config file override for Community2024 folder
if defined COMMUNITY2024_FOLDER_CONFIG (
  set "COMMUNITY=%COMMUNITY2024_FOLDER_CONFIG%"
)

REM 2. Config file override for Community folder
if not defined COMMUNITY if defined COMMUNITY_FOLDER_CONFIG (
  set "COMMUNITY=%COMMUNITY_FOLDER_CONFIG%"
)

REM 3. Auto-discovery from UserCfg.opt
if not defined COMMUNITY (
  set "USERCFG="
  for %%F in (
    "%APPDATA%\Microsoft Flight Simulator 2024\UserCfg.opt"
    "%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt"
    "%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalState\UserCfg.opt"
  ) do (
    if not defined USERCFG if exist "%%~F" set "USERCFG=%%~F"
  )

  set "PACKAGES_ROOT="
  if defined USERCFG (
    for /f "usebackq tokens=1,* delims= " %%A in (`findstr /i /c:"InstalledPackagesPath" "%USERCFG%"`) do (
      if /i "%%~A"=="InstalledPackagesPath" set "PACKAGES_ROOT=%%~B"
    )
  )

  if not defined PACKAGES_ROOT (
    set "PACKAGES_ROOT=%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages"
  )

  REM The SDK docs recommend Community2024 for MSFS 2024-only packages; Community remains
  REM as a compatibility fallback when a 2024-specific folder is not available.
  set "COMMUNITY=%PACKAGES_ROOT%\Community2024"
  if not exist "%COMMUNITY%" (
    set "COMMUNITY=%PACKAGES_ROOT%\Community"
  )
)

if not exist "%COMMUNITY%" (
  echo ERROR: No Community2024 or Community folder was found under: %PACKAGES_ROOT%
  echo The package was built anyway. Copy "%PANEL_DIR%Packages\msfs2024-ai-pilot" into
  echo your active MSFS packages folder by hand.
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