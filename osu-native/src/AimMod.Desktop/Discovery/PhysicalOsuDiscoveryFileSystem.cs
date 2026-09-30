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

    public string ReadAllText(string path, int maximumBytes) => Encoding.UTF8.GetString(ReadAllBytes(path, maximumBytes));

    public byte[] ReadAllBytes(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException($"File is larger than {maximumBytes} bytes.");

        // Size by the file, not the limit: a 32 MiB limit must not allocate 32 MiB for a tiny file.
        using var bytes = new MemoryStream((int)Math.Min(stream.Length, maximumBytes));
        byte[] chunk = new byte[81_920];
        long totalRead = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > maximumBytes)
                throw new InvalidDataException($"File grew beyond {maximumBytes} bytes while it was read.");
            bytes.Write(chunk, 0, read);
        }

        return bytes.ToArray();
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
