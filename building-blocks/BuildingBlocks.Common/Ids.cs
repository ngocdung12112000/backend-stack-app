using System.Security.Cryptography;

namespace BuildingBlocks.Common;

/// <summary>App-generated identifiers. GUID v7 (time-ordered) is implemented here because Guid.CreateVersion7 is .NET 9+.</summary>
public static class Ids
{
    public static Guid NewGuidV7() => NewGuidV7(DateTimeOffset.UtcNow);

    public static Guid NewGuidV7(DateTimeOffset timestamp)
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        var ms = timestamp.ToUnixTimeMilliseconds();
        b[0] = (byte)(ms >> 40);
        b[1] = (byte)(ms >> 32);
        b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16);
        b[4] = (byte)(ms >> 8);
        b[5] = (byte)ms;
        b[6] = (byte)((b[6] & 0x0F) | 0x70); // version 7
        b[8] = (byte)((b[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(b, bigEndian: true);
    }
}
