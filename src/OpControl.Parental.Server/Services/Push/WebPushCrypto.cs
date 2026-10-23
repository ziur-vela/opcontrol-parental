// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpControl.Parental.Server.Services.Push;

/// <summary>
/// Web Push without third-party libraries:
/// message encryption per RFC 8291 (aes128gcm, RFC 8188) and VAPID (RFC 8292) signed with ES256.
/// </summary>
public static class WebPushCrypto
{
    public const int RecordSize = 4096;
    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>Encrypts a payload for a subscription (p256dh public key and auth secret, both base64url).</summary>
    public static byte[] Encrypt(string p256dh, string auth, byte[] plaintext)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(PushBase64.Decode(p256dh), PushBase64.Decode(auth), plaintext, ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Deterministic overload, used by the RFC 8291 test vector.</summary>
    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, ECDiffieHellman asKey, byte[] salt)
    {
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04) throw new ArgumentException(null, nameof(uaPublic));
        if (authSecret.Length < 16) throw new ArgumentException(null, nameof(authSecret));
        if (salt.Length != 16) throw new ArgumentException(null, nameof(salt));
        if (plaintext.Length > RecordSize - 17 - 86) throw new ArgumentException(null, nameof(plaintext));

        using var uaKey = ECDiffieHellman.Create(PublicParameters(uaPublic));
        var asPublic = UncompressedPoint(asKey.ExportParameters(false));
        var ecdhSecret = asKey.DeriveRawSecretAgreement(uaKey.PublicKey);

        // Combine the ECDH secret with the auth secret (RFC 8291 section 3.3).
        var keyInfo = Concat(KeyInfoPrefix, uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        // Content encryption key and nonce (RFC 8188 section 2.2 and 2.3).
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // Single record: plaintext followed by the 0x02 last-record delimiter, no extra padding.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[^1] = 0x02;
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, ciphertext, tag);

        // Header: salt(16) | rs(4, big endian) | idlen(1) | keyid = as_public(65).
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, ciphertext, tag);
    }

    /// <summary>Creates a new VAPID key pair (base64url uncompressed public point, base64url private scalar).</summary>
    public static (string PublicKey, string PrivateKey) CreateVapidKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (PushBase64.Encode(UncompressedPoint(p)), PushBase64.Encode(p.D!));
    }

    /// <summary>Builds the "Authorization: vapid t=..., k=..." header value for a push endpoint.</summary>
    public static string VapidAuthorization(string endpoint, string subject, string publicKey, string privateKey, DateTimeOffset now)
    {
        var uri = new Uri(endpoint);
        var audience = uri.GetLeftPart(UriPartial.Authority);
        var header = PushBase64.Encode(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = PushBase64.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = audience,
            exp = now.AddHours(12).ToUnixTimeSeconds(),
            sub = subject
        }));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + claims);

        var pub = PushBase64.Decode(publicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
            D = PushBase64.Decode(privateKey)
        });
        // IEEE P1363 (r || s) is what JWS ES256 expects.
        var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{PushBase64.Encode(signature)}, k={publicKey}";
    }

    public static ECParameters PublicParameters(byte[] uncompressedPoint) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = uncompressedPoint[1..33], Y = uncompressedPoint[33..65] }
    };

    public static byte[] UncompressedPoint(ECParameters p)
    {
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1);
        p.Q.Y!.CopyTo(point, 33);
        return point;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}

public static class PushBase64
{
    public static string Encode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
