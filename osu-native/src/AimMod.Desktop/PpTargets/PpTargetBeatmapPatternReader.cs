using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Tests.Beatmaps;

namespace AimMod.Desktop.PpTargets;

internal sealed record PpTargetBeatmapFile(string Path, string ContentHash);

internal sealed record PpTargetBeatmapPatternGeometry(
    IReadOnlyList<PpPatternPoint> Points,
    double? HitRadius,
    double? ClockRate);

internal sealed class PpTargetBeatmapPatternReader(string cacheDirectory, int maximumFiles = 512, long maximumBytes = 256 * 1024 * 1024)
{
    internal const string Version = "playable-geometry-v2";
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    internal async Task<PpTargetBeatmapFile?> TryGetCachedFileAsync(int beatmapId, string? expectedHash, CancellationToken cancellationToken)
    {
        string path = sourcePath(beatmapId, expectedHash);
        try
        {
            if (!File.Exists(path))
                return null;
            // Without an upstream content hash, refresh the ID-based alias periodically.
            if (string.IsNullOrWhiteSpace(expectedHash) && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                return null;
            return await IdentifyAsync(path, expectedHash, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    internal static async Task<PpTargetBeatmapFile> IdentifyAsync(string path, string? expectedHash, CancellationToken cancellationToken)
    {
        validateHash(expectedHash);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > PpCalculationProtocol.MaximumBeatmapBytes)
            throw new InvalidDataException("Pattern extraction requires a non-empty, bounded .osu file.");
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        string contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            string actual = expectedHash.Length == 32 ? Convert.ToHexString(MD5.HashData(bytes)) : contentHash;
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The beatmap file no longer matches the requested content hash.");
        }
        return new PpTargetBeatmapFile(path, contentHash);
    }

    internal async Task RetainAsync(PpTargetBeatmapFile file, int beatmapId, string? expectedHash, CancellationToken cancellationToken)
    {
        string destination = sourcePath(beatmapId, expectedHash);
        if (Path.GetFullPath(file.Path).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;
        string temporaryPath = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await using (FileStream source = File.OpenRead(file.Path))
            await using (FileStream target = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destination, true);
            trimCache(destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"AimMod beatmap pattern file cache persistence failed: {error.Message}");
        }
        finally
        {
            deleteTemporary(temporaryPath);
        }
    }

    internal async Task<PpTargetBeatmapPatternGeometry> ReadAsync(PpTargetBeatmapFile file, IReadOnlyList<string> mods, CancellationToken cancellationToken)
    {
        string modKey = string.Join(',', PpTargetMods.Normalise(mods));
        string geometryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|{PpCalculationProtocol.EngineVersion}|{modKey}")));
        string path = Path.Combine(cacheDirectory, $"{file.ContentHash}-{geometryKey}.json");
        try
        {
            if (File.Exists(path))
            {
                await using FileStream stream = File.OpenRead(path);
                GeometryDocument? cached = await JsonSerializer.DeserializeAsync<GeometryDocument>(stream, json_options, cancellationToken).ConfigureAwait(false);
                if (cached is not null && cached.Version == Version && cached.ContentHash == file.ContentHash && cached.Mods == modKey
                    && cached.Geometry is { Points: not null } geometry)
                    return geometry;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A geometry cache failure must not prevent decoding the exact beatmap.
        }

        PpTargetBeatmapPatternGeometry result = Read(file.Path, mods, cancellationToken);
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await JsonSerializer.SerializeAsync(stream, new GeometryDocument(Version, file.ContentHash, modKey, result), json_options, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
            trimCache(path, file.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"AimMod beatmap geometry cache persistence failed: {error.Message}");
        }
        finally
        {
            deleteTemporary(temporaryPath);
        }
        return result;
    }

    internal static PpTargetBeatmapPatternGeometry Read(string path, IReadOnlyList<string> acronyms, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ensureStandardMode(path);
        _ = typeof(OsuRuleset).Assembly;
        var ruleset = new OsuRuleset();
        Mod[] mods = PpTargetMods.Normalise(acronyms).Select(acronym => ruleset.CreateModFromAcronym(acronym)
            ?? throw new InvalidDataException($"Pattern extraction does not support the {acronym} mod.")).ToArray();
        var working = new FlatWorkingBeatmap(path);
        if (working.BeatmapInfo.Ruleset.OnlineID != 0)
            throw new InvalidDataException("Pattern extraction supports osu!standard beatmaps only.");
        IBeatmap playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods, cancellationToken);
        var heads = new List<OsuHitObject>();
        var positions = new List<PpPatternPoint>();
        bool breakBefore = false;
        foreach (var hitObject in playable.HitObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hitObject is not OsuHitObject head || head is not (HitCircle or Slider))
            {
                breakBefore = true;
                continue;
            }
            // Head geometry does not model slider travel or connect across omitted spinners.
            heads.Add(head);
            positions.Add(new PpPatternPoint(head.StartTime, head.StackedPosition.X, head.StackedPosition.Y, breakBefore));
            breakBefore = false;
        }
        OsuHitObject[] objects = heads.ToArray();
        PpPatternPoint[] points = positions.ToArray();
        double? radius = objects.Length == 0 ? null : objects[0].Radius;
        if (objects.Any(hitObject => !double.IsFinite(hitObject.Radius) || hitObject.Radius <= 0 || Math.Abs(hitObject.Radius - radius!.Value) > 0.00001))
            radius = null;
        double? clockRate = 1;
        foreach (PpPatternPoint point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double rate = mods.OfType<IApplicableToRate>().Aggregate(1d, (current, mod) => mod.ApplyToRate(point.TimeMs, current));
            if (!double.IsFinite(rate) || rate <= 0 || (point != points[0] && Math.Abs(rate - clockRate!.Value) > 0.00001))
            {
                clockRate = null;
                break;
            }
            clockRate = rate;
        }
        return new PpTargetBeatmapPatternGeometry(points, radius, clockRate);
    }

    private string sourcePath(int beatmapId, string? hash)
    {
        if (beatmapId <= 0) throw new ArgumentOutOfRangeException(nameof(beatmapId));
        validateHash(hash);
        string identity = string.IsNullOrWhiteSpace(hash) ? "current" : hash.ToLowerInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(cacheDirectory, $"{beatmapId}-{key}.osu");
    }

    private static void validateHash(string? hash)
    {
        if (!string.IsNullOrWhiteSpace(hash) && (hash.Length is not (32 or 64) || !hash.All(Uri.IsHexDigit)))
            throw new ArgumentException("A beatmap checksum must contain 32 or 64 hexadecimal characters.", nameof(hash));
    }

    private static void ensureStandardMode(string path)
    {
        // LegacyBeatmapDecoder tolerates an unavailable ruleset by keeping the default.
        // Reject its raw mode first, rather than interpreting a mania map as standard.
        bool general = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (general) break;
                general = line.Equals("[General]", StringComparison.OrdinalIgnoreCase);
            }
            int separator = line.IndexOf(':');
            if (!general || separator < 0 || !line[..separator].Trim().Equals("Mode", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line[(separator + 1)..].Trim(), out int mode) || mode != 0)
                throw new InvalidDataException("Pattern extraction supports osu!standard beatmaps only.");
        }
    }

    private void trimCache(params string[] protectedPaths)
    {
        var files = new DirectoryInfo(cacheDirectory).EnumerateFiles()
            .Where(file => file.Extension is ".osu" or ".json").OrderBy(file => file.LastWriteTimeUtc).ToArray();
        long bytes = files.Sum(file => file.Length);
        int count = files.Length;
        foreach (FileInfo file in files)
        {
            if (count <= maximumFiles && bytes <= maximumBytes) break;
            if (protectedPaths.Any(path => file.FullName.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
            long length = file.Length;
            file.Delete();
            count--;
            bytes -= length;
        }
    }
    private static void deleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private sealed record GeometryDocument(string Version, string ContentHash, string Mods, PpTargetBeatmapPatternGeometry Geometry);
}
