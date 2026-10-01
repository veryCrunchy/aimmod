@echo off
setlocal
title AimMod for KovaaK's
cd /d "%~dp0"
echo Installing AimMod into KovaaK's. Close KovaaK's first.
echo.
"%~dp0files\ue4ss\Mods\AimModCore\service\AimMod.InGame.exe" --install %*
set "code=%errorlevel%"
echo.
if "%code%"=="0" (
  echo Done. Start KovaaK's from Steam.
  echo AimMod now updates itself. You can delete this folder.
  echo If AimMod stops loading after a KovaaK's update, run
  echo %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd with the game closed.
) else (
  echo The install did not finish; see the message above. Nothing was left half-installed.
)
pause
exit /b %code%
