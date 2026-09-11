using System.Security.Cryptography;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Models;

namespace AimMod.Desktop.Trainers;

internal static class TrainerAudioIdentity
{
    public static BeatmapInfo Attach(BeatmapInfo info, byte[] audio)
    {
        // Flat/generated maps have no database file manifest. osu!'s AudioEquals
        // compares file hashes, and two missing hashes compare equal. Give the
        // private map a real audio identity before MusicController sees it.
        info.BeatmapSet ??= new BeatmapSetInfo();
        foreach (var file in info.BeatmapSet.Files.Where(f => f.Filename == info.Metadata.AudioFile).ToArray())
            info.BeatmapSet.Files.Remove(file);
        info.BeatmapSet.Files.Add(new RealmNamedFileUsage(
            new RealmFile { Hash = Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant() }, info.Metadata.AudioFile));
        return info;
    }
}
