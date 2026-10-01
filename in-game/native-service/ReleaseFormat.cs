using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

// The in-game release format (see in-game/docs/install-lifecycle.md):
//   aimmod-release.json            inside every package zip: version, every
//                                  installed file with its SHA-256 and size,
//                                  UE4SS version and the tested game builds.
//   aimmod-ingame-<channel>.json   the update feed: newest version of a
//                                  channel, the package URL and SHA-256, the
//                                  release manifest SHA-256 and notes.
// Integrity is hash pinning: the feed (fetched over HTTPS from the configured
// release host) pins the zip and the manifest, the manifest pins every file.
sealed class ReleaseFormatException(string message) : Exception(message);

readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemanticVersion>
{
    static readonly Regex Pattern = new(@"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant);
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (text is null || text.Length > 64) return false;
        var match = Pattern.Match(text);
        if (!match.Success) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : "";
        if (pre.Split('.').Any(id => id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit))) return false;
        version = new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), pre);
        return true;
    }
    public static SemanticVersion Parse(string? text) => TryParse(text, out var v) ? v : throw new ReleaseFormatException("Invalid version.");
    public bool IsPrerelease => Prerelease.Length > 0;
    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        var a = Prerelease.Split('.'); var b = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = a[i].All(char.IsAsciiDigit); var bn = b[i].All(char.IsAsciiDigit);
            if (an && bn) { c = long.Parse(a[i]).CompareTo(long.Parse(b[i])); }
            else if (an != bn) { c = an ? -1 : 1; }
            else c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + Prerelease : "");
}

static class ReleasePaths
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$" };
    static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();
    // AimMod cosmetics paks: paks/~AimMod/<name>.pak, placed in the game's
    // Content\Paks\~AimMod (InstallLayout.Resolve). One folder, no
    // subfolders, never a patch pak (_P), which could override game files.
    public const string PakFolder = "paks/~AimMod";
    static readonly Regex PakName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.pak$", RegexOptions.CultureInvariant);
    public static bool IsPakName(string? name) =>
        name is not null && PakName.IsMatch(name) && !name.Contains("..") && !name.EndsWith("_P.pak", StringComparison.OrdinalIgnoreCase);
    public static bool IsPak(string? path) =>
        path is not null && path.StartsWith(PakFolder + "/", StringComparison.Ordinal) && IsPakName(path[(PakFolder.Length + 1)..]);

    // Release paths are relative to FPSAimTrainer\Binaries\Win64, written with
    // '/', and may only name the UE4SS proxy, something under ue4ss/, or an
    // AimMod cosmetics pak (paks/~AimMod/<name>.pak).
    public static bool IsAllowed(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\')) return false;
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.StartsWith(' ')) return false;
            if (part.IndexOfAny(Invalid) >= 0 || part.Any(char.IsControl)) return false;
            if (Reserved.Contains(part.Split('.')[0])) return false;
        }
        if (parts.Length == 1) return path.Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase);
        if (parts[0].Equals("paks", StringComparison.OrdinalIgnoreCase)) return IsPak(path);
        return parts[0].Equals("ue4ss", StringComparison.OrdinalIgnoreCase);
    }
    public static string ToWindows(string path) => path.Replace('/', '\\');
    public static string Combine(string root, string path)
    {
        if (!IsAllowed(path)) throw new ReleaseFormatException("Path not allowed in a release.");
        var full = Path.GetFullPath(Path.Combine(root, ToWindows(path)));
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ReleaseFormatException("Path escapes its folder.");
        return full;
    }
}

static class Sha256Hex
{
    public static bool IsValid(string? hex) => hex is { Length: 64 } && hex.All(Uri.IsHexDigit);
    public static string Of(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

sealed record ReleaseFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);
sealed record TestedBuild(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("steamBuildId")] long SteamBuildId);
sealed record GameRequirements(
    [property: JsonPropertyName("appId")] int AppId,
    [property: JsonPropertyName("minimumSteamBuildId")] long MinimumSteamBuildId,
    [property: JsonPropertyName("testedBuilds")] TestedBuild[] TestedBuilds);
sealed record Ue4ssInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("zipSha256")] string ZipSha256);

sealed record ReleaseManifest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("publishedAt")] string PublishedAt,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("ue4ss")] Ue4ssInfo Ue4ss,
    [property: JsonPropertyName("game")] GameRequirements Game,
    [property: JsonPropertyName("mods")] string[] Mods,
    [property: JsonPropertyName("files")] ReleaseFile[] Files)
{
    public const string SchemaName = "aimmod.ingame.release/1";
    public const int KovaaksAppId = 824270;
    public const int MaximumBytes = 1 << 20;
    static readonly Regex ModName = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);
    public SemanticVersion SemVer => SemanticVersion.Parse(Version);
    public static ReleaseManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaximumBytes) throw new ReleaseFormatException("Release manifest has an invalid size.");
        ReleaseManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, ReleaseJson.Options); }
        catch (JsonException) { throw new ReleaseFormatException("Release manifest is not valid JSON."); }
        if (manifest is null || manifest.Schema != SchemaName) throw new ReleaseFormatException("Unsupported release manifest.");
        manifest.Validate();
        return manifest;
    }
    void Validate()
    {
        if (!SemanticVersion.TryParse(Version, out _)) throw new ReleaseFormatException("Invalid release version.");
        if (Channel is not ("stable" or "beta")) throw new ReleaseFormatException("Invalid release channel.");
        if (Ue4ss is null || string.IsNullOrWhiteSpace(Ue4ss.Version) || !Sha256Hex.IsValid(Ue4ss.ZipSha256)) throw new ReleaseFormatException("Invalid UE4SS information.");
        if (Game is null || Game.AppId != KovaaksAppId || Game.MinimumSteamBuildId < 0 || Game.TestedBuilds is null || Game.TestedBuilds.Any(b => b is null || b.SteamBuildId <= 0 || string.IsNullOrWhiteSpace(b.Version)))
            throw new ReleaseFormatException("Invalid game requirements.");
        if (Mods is null || Mods.Length is 0 or > 16 || Mods.Any(m => m is null || !ModName.IsMatch(m)) || Mods.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Mods.Length)
            throw new ReleaseFormatException("Invalid mod list.");
        if (Files is null || Files.Length is 0 or > 4096) throw new ReleaseFormatException("Invalid file list.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || !ReleasePaths.IsAllowed(file.Path) || !Sha256Hex.IsValid(file.Sha256) || file.Size is < 0 or > 1L << 30)
                throw new ReleaseFormatException("Invalid file entry.");
            if (!seen.Add(file.Path)) throw new ReleaseFormatException("Duplicate file entry.");
        }
        if (!seen.Contains("dwmapi.dll")) throw new ReleaseFormatException("A release must contain the UE4SS proxy.");
        foreach (var mod in Mods)
            if (!Files.Any(f => f.Path.StartsWith("ue4ss/Mods/" + mod + "/", StringComparison.OrdinalIgnoreCase)))
                throw new ReleaseFormatException("A listed mod has no files.");
    }
}

sealed record FeedPackage(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);
sealed record UpdateFeed(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("publishedAt")] string PublishedAt,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("minimumSteamBuildId")] long MinimumSteamBuildId,
    [property: JsonPropertyName("manifestSha256")] string ManifestSha256,
    [property: JsonPropertyName("package")] FeedPackage Package)
{
    public const string SchemaName = "aimmod.ingame.feed/1";
    public const int MaximumBytes = 256 * 1024;
    public const long MaximumPackage = 512L << 20;
    public SemanticVersion SemVer => SemanticVersion.Parse(Version);
    public static UpdateFeed Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaximumBytes) throw new ReleaseFormatException("Update feed has an invalid size.");
        UpdateFeed? feed;
        try { feed = JsonSerializer.Deserialize<UpdateFeed>(json, ReleaseJson.Options); }
        catch (JsonException) { throw new ReleaseFormatException("Update feed is not valid JSON."); }
        if (feed is null || feed.Schema != SchemaName) throw new ReleaseFormatException("Unsupported update feed.");
        if (!SemanticVersion.TryParse(feed.Version, out _) || feed.Channel is not ("stable" or "beta")) throw new ReleaseFormatException("Invalid feed version.");
        if (!Sha256Hex.IsValid(feed.ManifestSha256) || feed.MinimumSteamBuildId < 0 || (feed.Notes?.Length ?? 0) > 16384) throw new ReleaseFormatException("Invalid feed entry.");
        if (feed.Package is null || !Sha256Hex.IsValid(feed.Package.Sha256) || feed.Package.Size is <= 0 or > MaximumPackage || !IsHttps(feed.Package.Url))
            throw new ReleaseFormatException("Invalid feed package.");
        return feed;
    }
    public static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && url!.Length <= 2048;
}

static class ReleaseJson
{
    public static readonly JsonSerializerOptions Options = new() { MaxDepth = 8, AllowTrailingCommas = false, ReadCommentHandling = JsonCommentHandling.Disallow };
}
