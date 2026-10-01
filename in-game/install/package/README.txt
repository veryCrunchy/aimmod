AimMod for KovaaK's (in-game)
=============================

Install (once)
1. Download AimMod-Setup.exe:
   https://github.com/verycrunchy/aimmod/releases/download/aimmod-ingame-stable/AimMod-Setup.exe
   (Beta: .../aimmod-ingame-beta/AimMod-Setup.exe)
2. Run it. It finds KovaaK's through Steam (or pick the folder yourself), always
   downloads the newest AimMod for the channel you choose (Stable or Beta),
   checks every file against the release's SHA-256 hashes and installs UE4SS and
   the AimMod mods. Files it replaces are backed up and restored when you uninstall.
   No administrator rights are needed unless your KovaaK's folder is protected.
3. Start KovaaK's from Steam.

Windows SmartScreen
AimMod-Setup.exe is not code-signed, so Windows may show "Windows protected your
PC". Click "More info", check that the file is AimMod-Setup.exe, then click
"Run anyway". The installer only installs files whose SHA-256 matches the
release on GitHub.

Install without the installer
Unzip AimMod-InGame-<version>.zip anywhere, close KovaaK's and double-click
Install-AimMod.cmd. You can delete the folder afterwards.

Updates
AimMod checks for updates when the game starts and every few hours,
downloads them in the background and installs them after you close KovaaK's.
You see "Update ready" in the AimMod workspace. Turn updates off or pick the
Beta channel in AimMod > Settings > Updates & repair.
With the game closed, AimMod-Setup.exe updates right away. Run it again any time,
or open "AimMod for KovaaK's" in Windows Settings > Apps.

After a KovaaK's update or "Verify integrity of game files"
If AimMod shows "AimMod needs a repair", click "Repair when I close KovaaK's",
or close the game and click Repair in AimMod-Setup.exe.
If AimMod does not load at all, close the game and run
  %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd
(paste that into the Windows Run box, Win+R). It reinstalls from the copy saved
at install time; nothing is downloaded.

Go back to the previous version:  Repair-AimMod.cmd -Rollback
Uninstall:                        AimMod-Setup.exe > Uninstall, Windows Settings > Apps,
                                  or Uninstall-AimMod.cmd
Your history, replays and settings in %LOCALAPPDATA%\AimMod are kept, unless you
tick "Also remove my AimMod data" in the installer.

Problems: the install log is %LOCALAPPDATA%\AimMod\KovaaksNative\updates\install.log
and the UE4SS log is ue4ss\UE4SS.log in FPSAimTrainer\Binaries\Win64.
