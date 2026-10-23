// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Services.Push;
using Xunit;

namespace OpControl.Parental.Tests;

public class PushTests
{
    // RFC 8291 Appendix A ("When I grow up, I want to be a watermelon").
    private const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";

    private static ECDiffieHellman ServerKey()
    {
        var pub = PushBase64.Decode(AsPublic);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
            D = PushBase64.Decode(AsPrivate)
        });
    }

    [Fact]
    public void Encryption_matches_rfc8291_test_vector()
    {
        using var key = ServerKey();
        var body = WebPushCrypto.Encrypt(PushBase64.Decode(UaPublic), PushBase64.Decode(AuthSecret),
            PushBase64.Decode(Plaintext), key, PushBase64.Decode(Salt));

        var expected = PushBase64.Decode(Header).Concat(PushBase64.Decode(Ciphertext)).ToArray();
        Assert.Equal(86, PushBase64.Decode(Header).Length);
        Assert.Equal(PushBase64.Encode(expected), PushBase64.Encode(body));
    }

    [Fact]
    public void Random_encryption_has_a_valid_header()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = PushBase64.Encode(WebPushCrypto.UncompressedPoint(ua.ExportParameters(false)));
        var body = WebPushCrypto.Encrypt(uaPublic, AuthSecret, Encoding.UTF8.GetBytes("{\"title\":\"hi\"}"));
        Assert.Equal(4096u, (uint)(body[16] << 24 | body[17] << 16 | body[18] << 8 | body[19]));
        Assert.Equal(65, body[20]);
        Assert.Equal(0x04, body[21]);
        Assert.Equal(86 + 15 + 16, body.Length); // header + payload + delimiter + tag
    }

    [Fact]
    public void Vapid_header_is_a_valid_es256_jwt_for_the_push_service()
    {
        var (publicKey, privateKey) = WebPushCrypto.CreateVapidKeys();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var header = WebPushCrypto.VapidAuthorization("https://fcm.googleapis.com/fcm/send/abc123", "mailto:parent@example.com", publicKey, privateKey, now);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith(", k=" + publicKey, header);
        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        using var claims = JsonDocument.Parse(PushBase64.Decode(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:parent@example.com", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

        var pub = PushBase64.Decode(publicKey);
        using var verify = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] }
        });
        Assert.True(verify.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), PushBase64.Decode(parts[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData("child-message", "messages")]
    [InlineData("tampering", "tampering")]
    [InlineData("daily-limit", "limits")]
    [InlineData("app-limit", "limits")]
    [InlineData("pin-override", "pin")]
    [InlineData("device-offline", "offline")]
    [InlineData("repeated-blocked-site", "blocked")]
    [InlineData("new-app", "newapp")]
    [InlineData("update-available", "other")]
    public void Alert_types_map_to_categories(string type, string category) =>
        Assert.Equal(category, PushService.Category(type));

    [Fact]
    public void Preferences_default_to_useful_alerts_but_not_new_apps()
    {
        var p = new PushPreference();
        Assert.True(PushService.Wants(p, "messages"));
        Assert.True(PushService.Wants(p, "tampering"));
        Assert.True(PushService.Wants(p, "limits"));
        Assert.False(PushService.Wants(p, "newapp"));
        p.Messages = false;
        Assert.False(PushService.Wants(p, "messages"));
    }

    [Fact]
    public void Coparents_need_the_matching_permission()
    {
        var owner = new ParentUser { RoleInFamily = FamilyRole.Owner, Permissions = ParentPermissions.None };
        var reports = new ParentUser { RoleInFamily = FamilyRole.Parent, Permissions = ParentPermissions.ViewReports };
        var messaging = new ParentUser { RoleInFamily = FamilyRole.Parent, Permissions = ParentPermissions.Messaging };

        Assert.True(PushService.Allowed(owner, "messages"));
        Assert.True(PushService.Allowed(owner, "tampering"));
        Assert.False(PushService.Allowed(reports, "messages"));
        Assert.True(PushService.Allowed(reports, "tampering"));
        Assert.True(PushService.Allowed(messaging, "messages"));
        Assert.False(PushService.Allowed(messaging, "limits"));
    }
}
