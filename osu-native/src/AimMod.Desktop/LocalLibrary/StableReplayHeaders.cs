using OsuParsers.Database.Objects;
using OsuParsers.Enums;
using osu.Game.IO.Legacy;

namespace AimMod.Desktop.LocalLibrary;

internal static class StableReplayHeaders
{
    public static Score? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Index metadata only. Never decompress cursor frames during library discovery.
            using var reader = new MetadataReader(file);
            var score = new Score { Ruleset = (Ruleset)reader.ReadByte(), OsuVersion = reader.ReadInt32() };
            if ((int)score.Ruleset is < 0 or > 3 || score.OsuVersion is <= 0 or >= 30000000) return null;
            score.BeatmapMD5Hash = reader.ReadString();
            score.PlayerName = reader.ReadString();
            score.ReplayMD5Hash = reader.ReadString();
            if (score.BeatmapMD5Hash.Length != 32 || !score.BeatmapMD5Hash.All(Uri.IsHexDigit)) return null;
            score.Count300 = reader.ReadUInt16();
            score.Count100 = reader.ReadUInt16();
            score.Count50 = reader.ReadUInt16();
            score.CountGeki = reader.ReadUInt16();
            score.CountKatu = reader.ReadUInt16();
            score.CountMiss = reader.ReadUInt16();
            score.ReplayScore = reader.ReadInt32();
            score.Combo = reader.ReadUInt16();
            score.PerfectCombo = reader.ReadBoolean();
            score.Mods = (Mods)reader.ReadInt32();
            reader.ReadString();
            score.ScoreTimestamp = reader.ReadDateTime();
            int length = reader.ReadInt32();
            long payloadEnd = reader.BaseStream.Position + (long)length;
            if (length <= 0 || payloadEnd > file.Length) return null;
            file.Position = payloadEnd;
            using var tail = new BinaryReader(file);
            if (score.OsuVersion >= 20140721 && file.Length - file.Position >= 8) score.ScoreId = tail.ReadInt64();
            else if (score.OsuVersion >= 20121008 && file.Length - file.Position >= 4) score.ScoreId = tail.ReadInt32();
            return score;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    public static string Key(Score score) => string.Join(':', score.BeatmapMD5Hash.ToLowerInvariant(),
        score.PlayerName.ToUpperInvariant(), score.ScoreTimestamp.Ticks, score.ReplayScore, (int)score.Mods,
        score.Combo, (int)score.Ruleset, score.Count300, score.Count100, score.Count50, score.CountGeki, score.CountKatu, score.CountMiss);

    private sealed class MetadataReader(Stream stream) : SerializationReader(stream)
    {
        public override string ReadString()
        {
            long start = BaseStream.Position;
            byte marker = ReadByte();
            if (marker == 0) return string.Empty;
            if (marker != 0x0b) throw new InvalidDataException("Invalid replay string marker.");
            int length = Read7BitEncodedInt();
            if (length < 0 || length > 1024 * 1024 || length > BaseStream.Length - BaseStream.Position)
                throw new InvalidDataException("Invalid replay metadata length.");
            BaseStream.Position = start;
            return base.ReadString();
        }
    }
}
