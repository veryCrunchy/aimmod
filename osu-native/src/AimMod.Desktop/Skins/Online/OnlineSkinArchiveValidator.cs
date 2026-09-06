using System.IO.Compression;
using System.Security.Cryptography;

namespace AimMod.Desktop.Skins.Online;

public sealed record OnlineSkinArchiveLimits(
    long MaximumArchiveBytes = 256L * 1024 * 1024,
    long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024,
    int MaximumEntries = 20_000,
    double MaximumCompressionRatio = 1_000);

public sealed record OnlineSkinArchiveValidation(
    bool IsValid,
    string? ErrorCode = null,
    string? Message = null,
    long ArchiveBytes = 0,
    long ExpandedBytes = 0,
    int EntryCount = 0,
    string? Sha256 = null);

public sealed class OnlineSkinArchiveValidator
{
    private static readonly HashSet<string> blockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml",
        ".vbs", ".vbe", ".vb", ".js", ".jse", ".mjs", ".cjs", ".wsf", ".wsh",
        ".msi", ".msp", ".mst", ".scr", ".com", ".lnk", ".pif", ".hta", ".cpl",
        ".ocx", ".sys", ".drv", ".reg", ".inf", ".ins", ".isp", ".sct", ".scf",
        ".url", ".application", ".appref-ms", ".msix", ".msixbundle", ".appx", ".appxbundle",
        ".sh", ".bash", ".zsh", ".fish", ".command", ".desktop", ".py", ".pyw",
        ".pyc", ".pl", ".pm", ".rb", ".jar", ".class", ".ps2", ".psc1", ".psc2",
    };

    private static readonly HashSet<string> skinSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "General", "Colours", "Fonts", "Mania", "CatchTheBeat",
    };

    private readonly OnlineSkinArchiveLimits limits;

    public OnlineSkinArchiveValidator(OnlineSkinArchiveLimits? limits = null)
    {
        this.limits = limits ?? new OnlineSkinArchiveLimits();
    }

    public async Task<OnlineSkinArchiveValidation> ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The skin archive path must be absolute.", nameof(path));
        var file = new FileInfo(path);
        if (!file.Exists)
            return invalid("archive_missing", "The downloaded skin archive is missing.");
        if (file.Length <= 0 || file.Length > limits.MaximumArchiveBytes)
            return invalid("archive_size", "The skin archive is empty or exceeds the configured size limit.");

        try
        {
            return await Task.Run(() => validate(path, file.Length, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            return invalid("invalid_zip", $"The downloaded file is not a valid .osk archive: {error.Message}");
        }
        catch (IOException error)
        {
            return invalid("archive_io", $"The downloaded skin archive could not be read: {error.Message}");
        }
        catch (OverflowException)
        {
            return invalid("expanded_size", "The expanded skin archive exceeds the configured size limit.");
        }
    }

    private OnlineSkinArchiveValidation validate(string path, long archiveBytes, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        long expanded = 0;
        List<ZipArchiveEntry> skinIniEntries = [];
        HashSet<string> entryPaths = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > limits.MaximumEntries)
                return invalid("too_many_entries", "The skin archive contains too many files.");
            if (!safeEntryName(entry.FullName))
                return invalid("unsafe_entry", "The skin archive contains an unsafe file path.");
            string normalized = entry.FullName.Replace('\\', '/');
            if (!entryPaths.Add(normalized.TrimEnd('/')))
                return invalid("duplicate_entry", "The skin archive contains duplicate file paths.");
            if (blockedExtensions.Contains(Path.GetExtension(normalized.TrimEnd('/'))))
                return invalid("unsafe_payload", "The skin archive contains an executable or script file.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                return invalid("unsafe_entry", "The skin archive contains a symbolic link.");
            expanded = checked(expanded + entry.Length);
            if (expanded > limits.MaximumExpandedBytes)
                return invalid("expanded_size", "The expanded skin archive exceeds the configured size limit.");
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio)
                return invalid("compression_ratio", "The skin archive contains a suspiciously compressed file.");
            if (string.Equals(Path.GetFileName(normalized), "skin.ini", StringComparison.OrdinalIgnoreCase))
            {
                skinIniEntries.Add(entry);
            }
        }
        if (skinIniEntries.Count == 0)
            return invalid("skin_ini_missing", "The archive does not contain a skin.ini file.");
        foreach (ZipArchiveEntry entry in skinIniEntries)
        {
            if (entry.Length > 1024 * 1024)
                return invalid("skin_ini_size", "The skin.ini file is unexpectedly large.");
            using Stream ini = entry.Open();
            using StreamReader reader = new(ini);
            if (!plausibleSkinIni(reader, cancellationToken))
                return invalid("skin_ini_invalid", "The skin.ini file does not contain a recognizable skin configuration.");
        }

        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new OnlineSkinArchiveValidation(true, ArchiveBytes: archiveBytes, ExpandedBytes: expanded, EntryCount: count, Sha256: hash);
    }

    private static bool safeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
            return false;
        string normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            return false;
        foreach (string part in (normalized.EndsWith('/') ? normalized[..^1] : normalized).Split('/'))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' '))
                return false;
            // Windows reserves device names even when an extension is present.
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789\u00b9\u00b2\u00b3".Contains(stem[3])))
                return false;
        }
        return !Path.IsPathRooted(name);
    }

    private static bool plausibleSkinIni(StreamReader reader, CancellationToken cancellationToken)
    {
        bool knownSection = false;
        bool hasSetting = false;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Any(c => char.IsControl(c) && c != '\t'))
                return false;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('['))
                knownSection = line.EndsWith(']') && skinSections.Contains(line[1..^1].Trim());
            else if (knownSection)
            {
                int separator = line.IndexOf(':');
                if (separator > 0 && line[..separator].Trim().Length > 0 && line[(separator + 1)..].Trim().Length > 0)
                    hasSetting = true;
            }
        }
        return hasSetting;
    }

    private static OnlineSkinArchiveValidation invalid(string code, string message) => new(false, code, message);
}
