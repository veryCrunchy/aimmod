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
        bool skinIni = false;
        ZipArchiveEntry? skinIniEntry = null;
        int count = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > limits.MaximumEntries)
                return invalid("too_many_entries", "The skin archive contains too many files.");
            if (!safeEntryName(entry.FullName))
                return invalid("unsafe_entry", "The skin archive contains an unsafe file path.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                return invalid("unsafe_entry", "The skin archive contains a symbolic link.");
            expanded = checked(expanded + entry.Length);
            if (expanded > limits.MaximumExpandedBytes)
                return invalid("expanded_size", "The expanded skin archive exceeds the configured size limit.");
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio)
                return invalid("compression_ratio", "The skin archive contains a suspiciously compressed file.");
            if (string.Equals(Path.GetFileName(entry.FullName), "skin.ini", StringComparison.OrdinalIgnoreCase))
            {
                skinIni = true;
                skinIniEntry ??= entry;
            }
        }
        if (!skinIni)
            return invalid("skin_ini_missing", "The archive does not contain a skin.ini file.");
        if (skinIniEntry!.Length > 1024 * 1024)
            return invalid("skin_ini_size", "The skin.ini file is unexpectedly large.");
        using (Stream ini = skinIniEntry.Open())
        {
            Span<byte> probe = stackalloc byte[1];
            _ = ini.Read(probe);
        }

        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new OnlineSkinArchiveValidation(true, ArchiveBytes: archiveBytes, ExpandedBytes: expanded, EntryCount: count, Sha256: hash);
    }

    private static bool safeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || name.Contains(':'))
            return false;
        string normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".."))
            return false;
        return !Path.IsPathRooted(name);
    }

    private static OnlineSkinArchiveValidation invalid(string code, string message) => new(false, code, message);
}
