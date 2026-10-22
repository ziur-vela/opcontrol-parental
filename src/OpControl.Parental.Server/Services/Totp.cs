// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Cryptography;

namespace OpControl.Parental.Server.Services;

public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() =>
        Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static bool Verify(string secret, string? code, DateTime? utcNow = null)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 6) return false;
        var now = utcNow ?? DateTime.UtcNow;
        var step = (long)(now - DateTime.UnixEpoch).TotalSeconds / 30;
        for (var offset = -1; offset <= 1; offset++)
            if (FixedTimeEquals(digits, Compute(secret, step + offset))) return true;
        return false;
    }

    public static string FormatSecret(string secret) =>
        string.Join(" ", Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - i * 4))));

    private static string Compute(string secret, long counter)
    {
        var key = Base32Decode(secret);
        Span<byte> buffer = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            buffer[i] = (byte)(counter & 0xff);
            counter >>= 8;
        }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(buffer.ToArray());
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (value % 1_000_000).ToString("D6");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(left), System.Text.Encoding.ASCII.GetBytes(right));

    private static string Base32Encode(byte[] data)
    {
        var output = new System.Text.StringBuilder();
        var bits = 0;
        var value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(Alphabet[(value << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        var text = input.Trim().Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(text.Length * 5 / 8);
        var bits = 0;
        var value = 0;
        foreach (var c in text)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) continue;
            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
