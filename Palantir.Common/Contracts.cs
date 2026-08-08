using System.Security.Cryptography;
using System.Text;

namespace Palantir.Common;

public record Marker(Guid Id, ushort Territory, byte Type, float X, float Y, float Z, int Confirmations = 0);

public static class MarkerType
{
    public const byte Trap = 1;
    public const byte Hoard = 2;
    public const byte Debug = 3;

    public static bool IsPersistable(byte type) => type is Trap or Hoard;

    public static string Name(byte type) => type switch
    {
        Trap => "trap",
        Hoard => "hoard",
        Debug => "debug marker",
        _ => "marker",
    };
}

public record PresenceStats(int Online, int InDungeon);

public record ServerMetadata(string Name, string Motd, int ProtocolVersion);

public record AuthRequest(Guid? AccountId);

public record AuthResponse(Guid AccountId, string Token);

public static class Protocol
{
    public const int Version = 2;
}

// type is in the key so a trap and a hoard can share a bucket and stay two markers
public static class MarkerId
{
    private static readonly Guid Namespace = new("6f9b1d2e-3c4a-5b6c-8d7e-9f0a1b2c3d4e");
    private static readonly byte[] NamespaceBytes = Swapped(Namespace.ToByteArray());

    public const float Limit = 4096f;

    public static (int X, int Y, int Z) Normalize(float x, float y, float z) =>
        ((int)MathF.Floor(x), (int)MathF.Floor(y), (int)MathF.Floor(z));
    
    public static bool IsValid(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
        MathF.Abs(x) < Limit && MathF.Abs(y) < Limit && MathF.Abs(z) < Limit;

    public static Guid For(ushort territory, byte type, float x, float y, float z)
    {
        var (nx, ny, nz) = Normalize(x, y, z);
        var key = $"{territory}:{type}:{nx}:{ny}:{nz}";

        // RFC 4122
        var hash = SHA1.HashData([.. NamespaceBytes, .. Encoding.UTF8.GetBytes(key)]);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        return new Guid(Swapped(bytes));
    }

    private static byte[] Swapped(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
        return guid;
    }
}
