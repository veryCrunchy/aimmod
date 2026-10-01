using System.Numerics;
using System.Security.Cryptography;

namespace AimMod.InGame;

// Ed25519 (RFC 8032, section 5.1) for release signatures. .NET 8 has no
// Ed25519 API, so this is the RFC's reference algorithm on BigInteger in
// extended coordinates. It only ever handles public data (release manifests
// and their signatures), so it is not constant time. Signing exists for the
// self-tests; release keys are used by openssl in CI, never by this service.
static class Ed25519
{
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly Point Base = BasePoint();

    readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);
    static readonly Point Identity = new(0, 1, 1, 0);

    static BigInteger Mod(BigInteger value) { var r = value % P; return r.Sign < 0 ? r + P : r; }
    static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);
    static BigInteger Number(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);
    static byte[] Bytes32(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (raw.Length > 32) throw new ArgumentOutOfRangeException(nameof(value));
        var result = new byte[32]; raw.CopyTo(result, 0); return result;
    }

    static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a; var f = d - c; var g = d + c; var h = b + a;
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }
    static Point Multiply(BigInteger scalar, Point point)
    {
        var result = Identity;
        while (scalar.Sign > 0)
        {
            if (!scalar.IsEven) result = Add(result, point);
            point = Add(point, point);
            scalar >>= 1;
        }
        return result;
    }
    static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

    static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2.IsZero) return sign == 0 ? BigInteger.Zero : null;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
        if (!Mod(x * x - x2).IsZero) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }
    static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, 0)!.Value;
        return new(x, y, 1, Mod(x * y));
    }
    static byte[] Compress(Point point)
    {
        var inverse = Inverse(point.Z);
        var x = Mod(point.X * inverse); var y = Mod(point.Y * inverse);
        return Bytes32(y | ((x & 1) << 255));
    }
    static Point? Decompress(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32) return null;
        var y = Number(encoded);
        var sign = (int)(y >> 255);
        y &= (BigInteger.One << 255) - 1;
        var x = RecoverX(y, sign);
        return x is null ? null : new Point(x.Value, y, 1, Mod(x.Value * y));
    }
    static BigInteger HashModL(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts) sha.AppendData(part);
        return Number(sha.GetHashAndReset()) % L;
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        var a = Decompress(publicKey);
        if (a is null) return false;
        var rEncoded = signature[..32].ToArray();
        var r = Decompress(rEncoded);
        if (r is null) return false;
        var s = Number(signature[32..]);
        if (s >= L) return false;
        var h = HashModL(rEncoded, publicKey.ToArray(), message.ToArray());
        return Equal(Multiply(s, Base), Add(r.Value, Multiply(h, a.Value)));
    }

    static (BigInteger Scalar, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
        var hash = SHA512.HashData(seed);
        var scalar = Number(hash.AsSpan(0, 32));
        scalar &= (BigInteger.One << 254) - 8;
        scalar |= BigInteger.One << 254;
        return (scalar, hash[32..]);
    }
    internal static byte[] PublicKey(ReadOnlySpan<byte> seed) => Compress(Multiply(Expand(seed).Scalar, Base));
    internal static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (scalar, prefix) = Expand(seed);
        var publicKey = Compress(Multiply(scalar, Base));
        var data = message.ToArray();
        var r = HashModL(prefix, data);
        var rEncoded = Compress(Multiply(r, Base));
        var h = HashModL(rEncoded, publicKey, data);
        var s = (r + h * scalar) % L;
        return [.. rEncoded, .. Bytes32(s)];
    }
}
