using System.Security.Cryptography;
using System.Text;

namespace Certiflow.SeedCorpus;

/// <summary>
/// Derives a stable GUID from a name.
/// <para>
/// The corpus must regenerate identically: the same supplier has to keep the same id across runs,
/// or every regeneration invalidates the seeded audit trail, the pre-computed extractions of
/// guardrail G8, and any screenshot taken from a previous run. <see cref="Guid.NewGuid"/> would
/// make the corpus disposable; this makes it reproducible.
/// </para>
/// <para>
/// This is RFC 4122 §4.3 name-based UUID generation (version 5, SHA-1). SHA-1 is used because the
/// spec says so and this is an identifier, not a security boundary — nothing here authenticates
/// anything, so collision resistance against a motivated attacker is not a property being relied
/// on.
/// </para>
/// </summary>
public static class DeterministicId
{
    /// <summary>A fixed namespace so ids from this generator cannot collide with any other source.</summary>
    private static readonly Guid CorpusNamespace = Guid.Parse("6f3d1a7c-2b45-4c8e-9a10-b7e2f1100000");

    public static Guid For(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var namespaceBytes = ToBigEndianBytes(CorpusNamespace);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var buffer = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(buffer, 0);
        nameBytes.CopyTo(buffer, namespaceBytes.Length);

#pragma warning disable CA5350 // Required by RFC 4122 for version-5 UUIDs; not a security control.
        var hash = SHA1.HashData(buffer);
#pragma warning restore CA5350

        var result = new byte[16];
        Array.Copy(hash, result, 16);

        // Stamp version 5 and the RFC 4122 variant.
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        return FromBigEndianBytes(result);
    }

    private static byte[] ToBigEndianBytes(Guid value)
    {
        var bytes = value.ToByteArray();
        SwapEndianness(bytes);
        return bytes;
    }

    private static Guid FromBigEndianBytes(byte[] bytes)
    {
        SwapEndianness(bytes);
        return new Guid(bytes);
    }

    /// <summary>
    /// .NET lays the first three GUID fields out little-endian; RFC 4122 hashes them big-endian.
    /// Skipping this still produces stable ids, but not ones matching any other implementation.
    /// </summary>
    /// <summary>
    /// A stable non-negative integer derived from a name, for generated numbers and picks.
    /// <para>
    /// Reads the hash bytes directly rather than going through <see cref="object.GetHashCode"/>:
    /// string and Guid hash codes are randomised per process or may change between runtime
    /// versions, either of which would silently break the reproducibility this class exists for.
    /// </para>
    /// </summary>
    public static int StableInt(string name)
    {
        var bytes = ToBigEndianBytes(For(name));

        return ((bytes[0] & 0x7F) << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private static void SwapEndianness(byte[] bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }
}
