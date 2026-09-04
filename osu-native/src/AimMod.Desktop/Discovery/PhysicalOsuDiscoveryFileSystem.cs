using System.Text;

namespace AimMod.Desktop.Discovery;

public sealed class PhysicalOsuDiscoveryFileSystem : IOsuDiscoveryFileSystem
{
    public DiscoveryEntry Inspect(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            bool symbolicLink = attributes.HasFlag(FileAttributes.ReparsePoint);
            long length = directory ? 0 : new FileInfo(path).Length;
            return new DiscoveryEntry(directory ? DiscoveryEntryKind.Directory : DiscoveryEntryKind.File, length, symbolicLink);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return new DiscoveryEntry(DiscoveryEntryKind.Missing);
        }
    }

    public string? CanonicalizeExisting(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return null;

            string current = root;
            string remainder = fullPath[root.Length..];

            foreach (string component in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(current, component);
                FileSystemInfo entry = Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate)
                    : File.Exists(candidate)
                        ? new FileInfo(candidate)
                        : throw new FileNotFoundException("Path component does not exist.", candidate);

                FileSystemInfo? target = entry.LinkTarget is null
                    ? null
                    : entry.ResolveLinkTarget(returnFinalTarget: true);
                current = target?.FullName ?? candidate;
            }

            return Path.GetFullPath(current);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException
                                               or FileNotFoundException or DirectoryNotFoundException
                                               or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public string ReadAllText(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException($"File is larger than {maximumBytes} bytes.");

        var bytes = new byte[maximumBytes + 1];
        int totalRead = 0;

        while (totalRead < bytes.Length)
        {
            int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0)
                break;
            totalRead += read;
        }

        if (totalRead > maximumBytes)
            throw new InvalidDataException($"File grew beyond {maximumBytes} bytes while it was read.");

        return Encoding.UTF8.GetString(bytes, 0, totalRead);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception error) when (error is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return DateTime.MinValue;
        }
    }
}
