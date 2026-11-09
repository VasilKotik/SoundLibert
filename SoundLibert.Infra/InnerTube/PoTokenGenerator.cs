using System.Security.Cryptography;
using System.Text;

namespace SoundLibert.Infra.InnerTube;

public static class PoTokenGenerator
{
    private const byte TokenVersion = 0x22;
    private const byte MagicHeader = 0x0A;
    private const byte InnerTag = 0x38;
    private const byte TimestampTag = 0x02;

    public static string GenerateContentToken(string identifier, string videoId)
    {
        return GenerateColdStartToken(identifier, videoId);
    }

    public static string GenerateSessionToken(string identifier)
    {
        return GenerateColdStartToken(identifier, "session");
    }

    private static string GenerateColdStartToken(string identifier, string clientState)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var identifierBytes = Encoding.UTF8.GetBytes(identifier);
        var stateBytes = Encoding.UTF8.GetBytes(clientState);

        var keyBytes = new byte[16];
        RandomNumberGenerator.Fill(keyBytes);

        var encryptedId = XorEncrypt(identifierBytes, keyBytes);
        var timestampBytes = EncodeLong(timestamp);

        var innerPayload = new List<byte>();
        innerPayload.Add(InnerTag);
        AppendVarInt(innerPayload, stateBytes.Length);
        innerPayload.AddRange(stateBytes);
        innerPayload.Add(TimestampTag);
        AppendVarInt(innerPayload, timestampBytes.Length);
        innerPayload.AddRange(timestampBytes);

        var tokenPayload = new List<byte>();
        tokenPayload.Add(MagicHeader);
        AppendVarInt(tokenPayload, keyBytes.Length);
        tokenPayload.AddRange(keyBytes);
        tokenPayload.Add(TokenVersion);
        AppendVarInt(tokenPayload, encryptedId.Length);
        tokenPayload.AddRange(encryptedId);
        tokenPayload.AddRange(innerPayload);

        return Convert.ToBase64String(tokenPayload.ToArray())
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] XorEncrypt(byte[] data, byte[] key)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }
        return result;
    }

    private static byte[] EncodeLong(long value)
    {
        var buf = new byte[8];
        long v = value;
        for (int i = 0; i < 8; i++)
        {
            buf[i] = (byte)(v & 0xFF);
            v >>= 8;
        }
        int len = 8;
        while (len > 1 && buf[len - 1] == 0) len--;
        return buf[..len];
    }

    private static void AppendVarInt(List<byte> list, int value)
    {
        var v = value;
        while (v >= 0x80)
        {
            list.Add((byte)(v | 0x80));
            v >>= 7;
        }
        list.Add((byte)v);
    }
}
