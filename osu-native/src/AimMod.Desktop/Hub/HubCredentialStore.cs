using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AimMod.Desktop.Hub;

public sealed record HubCredential(string UploadToken, string AccountLabel, DateTimeOffset LinkedAt);

public interface IHubCredentialStore
{
    HubCredential? Load();
    Task SaveAsync(HubCredential credential, CancellationToken cancellationToken = default);
    void Clear();
}

public interface IHubSecretProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}

public sealed class FileHubCredentialStore : IHubCredentialStore
{
    private const int current_version = 1;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly IHubSecretProtector protector;

    public FileHubCredentialStore(string path, IHubSecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Hub credential path must be absolute.", nameof(path));
        this.path = path;
        this.protector = protector ?? PlatformHubSecretProtector.Instance;
    }

    public HubCredential? Load()
    {
        try
        {
            if (!File.Exists(path))
                return null;
            byte[] protectedBytes = File.ReadAllBytes(path);
            byte[] payload = protector.Unprotect(protectedBytes);
            CredentialDocument? document = JsonSerializer.Deserialize<CredentialDocument>(payload, json_options);
            return document?.Version == current_version
                   && !string.IsNullOrWhiteSpace(document.Credential.UploadToken)
                ? document.Credential
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException or Win32Exception)
        {
            return null;
        }
    }

    public async Task SaveAsync(HubCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.UploadToken))
            throw new ArgumentException("An upload token is required.", nameof(credential));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new CredentialDocument(current_version, credential), json_options);
        byte[] protectedBytes = protector.Protect(payload);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            restrictUnixPermissions(temporaryPath);
            File.Move(temporaryPath, path, true);
            restrictUnixPermissions(path);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Clear()
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void restrictUnixPermissions(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record CredentialDocument(int Version, HubCredential Credential);
}

public sealed class PlatformHubSecretProtector : IHubSecretProtector
{
    public static PlatformHubSecretProtector Instance { get; } = new();

    public byte[] Protect(byte[] value) => OperatingSystem.IsWindows()
        ? WindowsDataProtection.Protect(value)
        : value.ToArray();

    public byte[] Unprotect(byte[] value) => OperatingSystem.IsWindows()
        ? WindowsDataProtection.Unprotect(value)
        : value.ToArray();

    private static class WindowsDataProtection
    {
        private const int cryptprotect_ui_forbidden = 0x1;

        public static byte[] Protect(byte[] value) => transform(value, protect: true);
        public static byte[] Unprotect(byte[] value) => transform(value, protect: false);

        private static byte[] transform(byte[] value, bool protect)
        {
            GCHandle handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { Size = value.Length, Data = handle.AddrOfPinnedObject() };
                bool success = protect
                    ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, cryptprotect_ui_forbidden, out DataBlob output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, cryptprotect_ui_forbidden, out output);
                if (!success)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    byte[] result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    if (output.Data != IntPtr.Zero)
                        LocalFree(output.Data);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
