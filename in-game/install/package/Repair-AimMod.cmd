@echo off
setlocal
title AimMod repair
rem Re-applies the AimMod install from the copy saved at install time
rem (%LOCALAPPDATA%\AimMod\KovaaksNative\package\current), for example after a
rem KovaaK's update or "Verify integrity of game files". Nothing is downloaded.
rem   Repair-AimMod.cmd             repair
rem   Repair-AimMod.cmd -Rollback   go back to the version before the last update
rem   Repair-AimMod.cmd -Uninstall  remove AimMod (history and replays are kept)
rem KovaaK's is found through Steam; set AIMMOD_GAME_DIR to its folder otherwise.
set "MODE=--repair"
if /i "%~1"=="-Rollback" set "MODE=--rollback"
if /i "%~1"=="-Uninstall" set "MODE=--uninstall"
set "SOURCE=%LOCALAPPDATA%\AimMod\KovaaksNative\package\current"
if not exist "%SOURCE%\aimmod-release.json" set "SOURCE=%~dp0"
if "%SOURCE:~-1%"=="\" set "SOURCE=%SOURCE:~0,-1%"
set "EXE=%SOURCE%\files\ue4ss\Mods\AimModCore\service\AimMod.InGame.exe"
if not exist "%EXE%" (
  echo AimMod's saved copy was not found. Download AimMod again and run Install-AimMod.cmd.
  pause
  exit /b 1
)
rem Run a temporary copy so the saved package itself can be replaced.
set "RUN=%TEMP%\AimMod-Repair-%RANDOM%%RANDOM%"
mkdir "%RUN%" >nul 2>&1
copy /y "%SOURCE%\files\ue4ss\Mods\AimModCore\service\*" "%RUN%\" >nul
if errorlevel 1 (
  echo Could not prepare the repair tool.
  pause
  exit /b 1
)
set "DATA=%LOCALAPPDATA%\AimMod\KovaaksNative"
set "GAME="
if defined AIMMOD_GAME_DIR set GAME=--game-dir "%AIMMOD_GAME_DIR%"
echo Close KovaaK's before continuing.
echo.
if "%MODE%"=="--repair" (
  "%RUN%\AimMod.InGame.exe" --repair --package "%SOURCE%" --output "%DATA%" %GAME%
) else (
  "%RUN%\AimMod.InGame.exe" %MODE% --output "%DATA%" %GAME%
)
set "code=%errorlevel%"
rmdir /s /q "%RUN%" >nul 2>&1
echo.
if not "%code%"=="0" (echo Not finished; see the message above.) else if "%MODE%"=="--uninstall" (echo Done.) else (echo Done. Start KovaaK's from Steam.)
pause
exit /b %code%
