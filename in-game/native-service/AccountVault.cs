using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;

namespace AimMod.InGame;

record HubAccount(string Handle, string Label, string ExternalId, string Token);
record PendingLink(string DeviceCode, string UserCode, string ApprovalUrl, DateTimeOffset Expires, DateTimeOffset NextPoll, int Interval);

// DPAPI binds the complete account record to this Windows user. Neither the
// token nor device code is sent through the game's file protocol or logs.
static class AccountVault
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) }; Blob result = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var ok = protect ? CryptProtectData(ref input, "AimMod account", 0, 0, 0, 1, out result)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out result);
            if (!ok) throw new IOException("Could not access the saved account.", new Win32Exception(Marshal.GetLastWin32Error()));
            var bytes = new byte[result.Length]; Marshal.Copy(result.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            Marshal.Copy(new byte[value.Length], 0, input.Data, value.Length); Marshal.FreeHGlobal(input.Data);
            if (result.Data != 0) { Marshal.Copy(new byte[result.Length], 0, result.Data, result.Length); LocalFree(result.Data); }
        }
    }
    public static HubAccount? Read(string path) => ReadRecord<HubAccount>(path);
    public static T? ReadRecord<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new IOException("Invalid saved account.");
        var bytes = Transform(File.ReadAllBytes(path), false);
        try { return JsonSerializer.Deserialize<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void Save<T>(string path, T account)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(account);
        try { File.WriteAllBytes(path + ".next", Transform(bytes, true)); File.Move(path + ".next", path, true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
