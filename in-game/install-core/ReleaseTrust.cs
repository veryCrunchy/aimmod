using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AimMod.InGame;

static class ReleaseTrust
{
    // Maintainer: populate release-keys.json with key ID -> base64 SPKI public key.
    // An empty map is the legacy migration build. Pinning any key requires signatures.
    static readonly IReadOnlyDictionary<string, string> PinnedKeys = LoadKeys();
    static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> FixtureKeys = new();
    static IReadOnlyDictionary<string, string> Keys => FixtureKeys.Value ?? PinnedKeys;
    internal static IDisposable LegacyFixtures()
    {
        var previous = FixtureKeys.Value;
        FixtureKeys.Value = new Dictionary<string, string>();
        return new FixtureScope(previous);
    }
    sealed class FixtureScope(IReadOnlyDictionary<string, string>? previous) : IDisposable
    {
        public void Dispose() => FixtureKeys.Value = previous;
    }
    static IReadOnlyDictionary<string, string> LoadKeys()
    {
        using var stream = typeof(ReleaseTrust).Assembly.GetManifestResourceStream("AimMod.InGame.release-keys.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    public static void Remember(ReadOnlySpan<byte> bytes, string root) => Remember(bytes, root, Keys);
    internal static void Remember(ReadOnlySpan<byte> bytes, string root, IReadOnlyDictionary<string, string> keys)
    {
        Verify(bytes, keys, DateTimeOffset.UtcNow);
        if (keys.Count == 0) return;
        using var document = JsonDocument.Parse(bytes.ToArray());
        var data = document.RootElement;
        var channel = data.GetProperty("channel").GetString();
        if (channel is not ("stable" or "beta")) throw new ReleaseFormatException("Invalid signed channel.");
        var counter = data.GetProperty("releaseCounter").GetInt64();
        var identity = data.GetProperty("version").GetString() + ":" + data.GetProperty("manifestSha256").GetString();
        var path = Path.Combine(root, "accepted-" + channel + ".json");
        if (File.Exists(path))
        {
            var previous = JsonSerializer.Deserialize<Accepted>(File.ReadAllBytes(path))!;
            if (counter < previous.Counter || (counter == previous.Counter && identity != previous.Identity))
                throw new ReleaseFormatException("Release metadata would roll back the accepted channel counter.");
        }
        Directory.CreateDirectory(root);
        AtomicFile.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(new Accepted(counter, identity)), durable: true);
    }
    sealed record Accepted(long Counter, string Identity);
    public static void Verify(ReadOnlySpan<byte> bytes) => Verify(bytes, Keys, DateTimeOffset.UtcNow);
    internal static void Verify(ReadOnlySpan<byte> bytes, IReadOnlyDictionary<string, string> keys, DateTimeOffset now)
    {
        if (keys.Count == 0) return;
        try
        {
            var document = JsonNode.Parse(bytes)!.AsObject();
            var signature = document["signature"]!.AsObject();
            var keyId = signature["keyId"]!.GetValue<string>();
            if (!keys.TryGetValue(keyId, out var publicKey)) throw new ReleaseFormatException("Unknown release signing key.");
            var payload = Convert.FromBase64String(signature["payload"]!.GetValue<string>());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value || !key.VerifyData(payload, Convert.FromBase64String(signature["value"]!.GetValue<string>()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new ReleaseFormatException("Invalid release signature.");
            document.Remove("signature");
            if (!JsonNode.DeepEquals(document, JsonNode.Parse(payload))) throw new ReleaseFormatException("Release metadata was changed.");
            if (document["releaseCounter"]!.GetValue<long>() <= 0 || DateTimeOffset.Parse(document["expiresAt"]!.GetValue<string>()) <= now)
                throw new ReleaseFormatException("Release metadata has expired or has an invalid counter.");
        }
        catch (ReleaseFormatException) { throw; }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidOperationException or NullReferenceException or ArgumentException)
        { throw new ReleaseFormatException("Signed release metadata is required and must be valid."); }
    }
}
