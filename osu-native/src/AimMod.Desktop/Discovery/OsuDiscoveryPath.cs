namespace AimMod.Desktop.Discovery;

public static class OsuDiscoveryPath
{
    public static string Combine(OsuHostPlatform platform, string root, params string[] components)
    {
        char separator = platform == OsuHostPlatform.Windows ? '\\' : '/';
        string value = root.TrimEnd('/', '\\');

        foreach (string component in components)
        {
            if (string.IsNullOrWhiteSpace(component))
                continue;

            value += separator + component.Trim('/', '\\');
        }

        return value;
    }

    public static bool IsAbsolute(OsuHostPlatform platform, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (platform != OsuHostPlatform.Windows)
            return path[0] == '/';

        bool driveRooted = path.Length >= 3
                           && char.IsAsciiLetter(path[0])
                           && path[1] == ':'
                           && isSeparator(path[2]);

        if (driveRooted)
            return true;

        if (path.Length < 5 || !isSeparator(path[0]) || !isSeparator(path[1]))
            return false;

        string[] uncParts = path[2..].Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return uncParts.Length >= 2;
    }

    public static bool IsWithin(OsuHostPlatform platform, string child, string root)
    {
        StringComparison comparison = platform == OsuHostPlatform.Windows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        char separator = platform == OsuHostPlatform.Windows ? '\\' : '/';
        string normalisedRoot = normaliseSeparators(platform, root).TrimEnd(separator);
        string normalisedChild = normaliseSeparators(platform, child).TrimEnd(separator);

        return string.Equals(normalisedChild, normalisedRoot, comparison)
               || normalisedChild.StartsWith(normalisedRoot + separator, comparison);
    }

    public static StringComparer Comparer(OsuHostPlatform platform) => platform == OsuHostPlatform.Windows
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string normaliseSeparators(OsuHostPlatform platform, string value) => platform == OsuHostPlatform.Windows
        ? value.Replace('/', '\\')
        : value.Replace('\\', '/');

    private static bool isSeparator(char value) => value is '/' or '\\';
}
