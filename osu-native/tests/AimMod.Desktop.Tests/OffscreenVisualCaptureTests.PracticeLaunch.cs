using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(false)]
    [TestCase(true)]
    [Explicit("Imports and plays a synthetic practice archive on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task ImportedPracticeDifficultyReachesPlayerAndRecordsCompletion(bool assisted)
    {
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, failure) =>
            new PracticeLaunchGame(host, assisted, success, failure), TimeSpan.FromSeconds(40));
    }

    private sealed partial class PracticeLaunchGame(GameHost host, bool assisted, Action success, Action<Exception> failure)
        : AimModGame(AimModLaunchOptions.Home, new InMemoryLocalLibrarySource([], []))
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private PracticeMapLibrary library = null!;
        private SavedPracticeMap saved = null!;
        private PracticeDifficultyIdentity selected = null!;
        private bool sawPlayer;
        private int polls;

        protected override void LoadComplete()
        {
            base.LoadComplete();
            LocalConfig.SetValue(OsuSetting.AudioOffset, 0d);
            Scheduler.AddDelayed(() =>
            {
                try
                {
                    var stableRoot = Storage.GetFullPath("synthetic-controls", true);
                    Directory.CreateDirectory(stableRoot);
                    File.WriteAllText(Path.Combine(stableRoot, "osu!." + Environment.UserName + ".cfg"),
                        "keyOsuLeft = A\nkeyOsuRight = S\nOffset = -37\nMouseDisableButtons = 1");
                    typeof(AimModGame).GetField("trainerStableRoot", flags)!.SetValue(this, stableRoot);
                    typeof(AimModGame).GetField("trainerLazerRoot", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetField("beatmapDestinationService", flags)!.SetValue(this, null);
                    typeof(AimModGame).GetMethod("showCoaching", flags)!.Invoke(this, null);
                    library = new PracticeMapLibrary(Storage.GetFullPath("practice-maps", true));
                    const string setId = "0123456789abcdef0123456789abcdef";
                    var archive = library.ArchivePath(setId);
                    Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                    var identities = new List<PracticeDifficultyIdentity>();
                    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                    {
                        using (var audio = zip.CreateEntry("music.ogg").Open())
                            audio.Write(TrainerAudio.Asset("training-midnight-pulse.ogg"));
                        for (int variant = 0; variant < 2; variant++)
                        {
                            string name = variant == 0 ? "Other difficulty" : "Selected difficulty";
                            string text = "osu file format v14\n[General]\nAudioFilename: music.ogg\nMode: 0\n[Metadata]\nTitle: Synthetic practice\nArtist: AimMod\nCreator: Test\nVersion: " + name
                                + "\n[Difficulty]\nHPDrainRate:0\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,500,4,1,0,100,1,0\n[HitObjects]\n"
                                + string.Join("\n", Enumerable.Range(0, 10).Select(i => $"{128 + variant * 128},192,{2000 + i * 500},1,0,0:0:0:0:")) + "\n";
                            byte[] bytes = Encoding.UTF8.GetBytes(text);
                            using (var entry = zip.CreateEntry(name + ".osu").Open()) entry.Write(bytes);
                            identities.Add(new(name, PracticeDrillType.LongJumps,
                                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
                                2000, 6500, true, assisted ? PracticeBreakdownVariant.AimFocus : PracticeBreakdownVariant.ReducedMovement,
                                "section-a", assisted ? "RX" : ""));
                        }
                    }
                    selected = identities[1];
                    saved = new(setId, "Synthetic practice", "Hard", PracticeDrillType.LongJumps,
                        DateTimeOffset.UtcNow.AddMinutes(-1), 2000, 6500, 6500, 1, 10,
                        Tracking: new("Practice Player", 0, 0, "synthetic-source", Guid.Empty, "", false, [], identities));
                    library.Save(saved);
                    typeof(AimModGame).GetMethod("startPracticeDifficulty", flags)!.Invoke(this, [saved, selected]);
                    Scheduler.AddDelayed(check, 300);
                }
                catch (Exception error) { failure(error); host.Exit(); }
            }, 1500);
        }

        private void check()
        {
            try
            {
                var player = (NativeTrainerPlayer?)typeof(AimModGame).GetField("trainerPlayer", flags)!.GetValue(this);
                if (player?.Ready == true)
                {
                    sawPlayer = true;
                    Assert.Multiple(() =>
                    {
                        Assert.That(Beatmap.Value.BeatmapInfo.MD5Hash, Is.EqualTo(selected.Md5));
                        Assert.That(Beatmap.Value.BeatmapInfo.Hash, Is.EqualTo(selected.Sha256));
                        Assert.That(Beatmap.Value.Beatmap.HitObjects.Count, Is.EqualTo(10));
                        Assert.That(Beatmap.Value.Track.Length, Is.GreaterThan(6500));
                        Assert.That(SelectedMods.Value.Any(m => m is OsuModRelax), Is.EqualTo(assisted));
                        Assert.That(SelectedMods.Value.Any(m => m is OsuModNoFail), Is.True);
                        Assert.That(LocalConfig.Get<double>(OsuSetting.AudioOffset), Is.EqualTo(-37));
                    });
                }
                var attempts = library.LoadProgress(saved.Id).Attempts;
                if (attempts.Count > 0)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(sawPlayer, Is.True);
                        Assert.That(player, Is.Null, "The native player should return to coaching after the last note.");
                        Assert.That(attempts, Has.Count.EqualTo(1));
                        Assert.That(attempts[0].Difficulty, Is.EqualTo(selected.Name));
                        Assert.That(attempts[0].Assisted, Is.EqualTo(assisted));
                        Assert.That(attempts[0].Original, Is.False);
                        Assert.That(attempts[0].Passed, Is.True);
                        Assert.That(LocalConfig.Get<double>(OsuSetting.AudioOffset), Is.EqualTo(0));
                        Assert.That(File.Exists(library.ArchivePath(saved.Id)), Is.True);
                    });
                    success(); host.Exit(); return;
                }
                if (++polls > 100) throw new TimeoutException($"Practice did not complete. Player reached: {sawPlayer}");
                Scheduler.AddDelayed(check, 300);
            }
            catch (Exception error) { failure(error); host.Exit(); }
        }
    }
}
