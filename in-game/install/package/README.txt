AimMod for KovaaK's (in-game)
=============================

Install (once)
1. Close KovaaK's.
2. Unzip this folder anywhere and double-click Install-AimMod.cmd.
   It finds KovaaK's through Steam and installs UE4SS and the AimMod mods.
   Files it replaces are backed up and restored by the uninstaller.
3. Start KovaaK's from Steam. You can delete this folder afterwards.

Updates
AimMod checks for signed updates when the game starts and every few hours,
downloads them in the background and installs them after you close KovaaK's.
You see "Update ready" in the AimMod workspace. Turn updates off or pick the
Beta channel in AimMod > Settings > Updates & repair.

After a KovaaK's update or "Verify integrity of game files"
If AimMod shows "AimMod needs a repair", click "Repair when I close KovaaK's".
If AimMod does not load at all, close the game and run
  %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd
(paste that into the Windows Run box, Win+R). It reinstalls from the copy saved
at install time; nothing is downloaded.

Go back to the previous version:  Repair-AimMod.cmd -Rollback
Uninstall:                        Uninstall-AimMod.cmd (or Repair-AimMod.cmd -Uninstall)
Your history, replays and settings in %LOCALAPPDATA%\AimMod are kept.

Problems: the install log is %LOCALAPPDATA%\AimMod\KovaaksNative\updates\install.log
and the UE4SS log is ue4ss\UE4SS.log in FPSAimTrainer\Binaries\Win64.
