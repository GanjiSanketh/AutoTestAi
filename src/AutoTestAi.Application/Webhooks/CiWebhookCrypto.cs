using System.Security.Cryptography;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Shared webhook cryptography helpers (Phase 3 Slice 3B). No provider
/// semantics here — each adapter decides which mechanism applies.
/// </summary>
public static class CiWebhookCrypto
{
    /// <summary>Constant-time buffer comparison (HMAC/token/password).</summary>
    public static bool FixedTimeEquals(byte[] left, byte[] right)
        => CryptographicOperations.FixedTimeEquals(left, right);

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public static byte[] HmacSha256(byte[] key, byte[] body)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(body);
    }

    public static string Sha256Hex(byte[] body)
        => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    /// <summary>Parses "sha256=&lt;hex&gt;" (GitHub X-Hub-Signature-256 form).</summary>
    public static bool TryParseSha256HexSignature(string? header, out byte[] signature)
    {
        signature = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(header))
            return false;
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var hex = header[prefix.Length..].Trim();
        if (hex.Length is 0 or > 256 || hex.Length % 2 != 0)
            return false;
        try
        {
            signature = Convert.FromHexString(hex);
            return signature.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
