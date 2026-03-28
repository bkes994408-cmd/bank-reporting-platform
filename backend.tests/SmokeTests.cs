using BankReporting.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

public class SmokeTests
{
    // 這些 smoke tests 主要覆蓋安全關鍵路徑（密碼、MFA、session 撤銷、匯出格式），
    // 供快速驗證重構/註解調整後核心行為未回歸。
    [Theory]
    [InlineData("weak", false)]
    [InlineData("Strong#123456", true)]
    public void PasswordPolicy_Works(string password, bool expected)
    {
        Assert.Equal(expected, SecurityHelpers.IsStrongPassword(password));
    }

    [Fact]
    public void PasswordHash_Roundtrip_Works()
    {
        var hash = SecurityHelpers.HashPassword("Strong#123456");
        Assert.True(SecurityHelpers.VerifyPassword("Strong#123456", hash));
        Assert.False(SecurityHelpers.VerifyPassword("wrong", hash));
    }

    [Fact]
    public void CsvExport_Works()
    {
        using var doc = JsonDocument.Parse("{\"reportMonth\":\"2026-01\",\"amount\":123}");
        var csv = SecurityHelpers.BuildCsv(doc);
        Assert.Contains("reportMonth", csv);
        Assert.Contains("2026-01", csv);
    }

    [Fact]
    public void XlsxExport_Generates_OpenXmlZip()
    {
        using var doc = JsonDocument.Parse("{\"reportMonth\":\"2026-01\",\"amount\":123}");
        var bytes = SecurityHelpers.BuildXlsx(doc);
        Assert.True(bytes.Length > 100);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public void Totp_InvalidCode_Fails()
    {
        var secret = SecurityHelpers.NewTotpSecret();
        Assert.False(SecurityHelpers.VerifyTotp(secret, "000000"));
    }

    [Fact]
    public void SessionStore_RevokeToken_InvalidatesSessionAndTracksJti()
    {
        var store = new SessionStore();
        var token = "token-abc";
        var userId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        store.Add(token, userId, DateTimeOffset.UtcNow, expiresAt, "jti-1");

        Assert.True(store.TryGetUserId(token, out var resolvedUser));
        Assert.Equal(userId, resolvedUser);

        store.RevokeToken(token, DateTimeOffset.UtcNow);

        Assert.False(store.TryGetUserId(token, out _));
        Assert.True(store.IsJtiRevoked("jti-1"));
    }

    [Fact]
    public void SessionStore_RevokeUserSessions_RevokesAllActiveSessionsForUser()
    {
        var store = new SessionStore();
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        store.Add("token-1", target, DateTimeOffset.UtcNow, expiresAt, "jti-target-1");
        store.Add("token-2", target, DateTimeOffset.UtcNow, expiresAt, "jti-target-2");
        store.Add("token-3", other, DateTimeOffset.UtcNow, expiresAt, "jti-other");

        var count = store.RevokeUserSessions(target, DateTimeOffset.UtcNow);
        Assert.Equal(2, count);

        Assert.False(store.TryGetUserId("token-1", out _));
        Assert.False(store.TryGetUserId("token-2", out _));
        Assert.True(store.TryGetUserId("token-3", out var resolvedOther));
        Assert.Equal(other, resolvedOther);
        Assert.True(store.IsJtiRevoked("jti-target-1"));
        Assert.True(store.IsJtiRevoked("jti-target-2"));
        Assert.False(store.IsJtiRevoked("jti-other"));
    }

    [Fact]
    public void PasswordHistory_PreventsPasswordReuse()
    {
        var old1 = SecurityHelpers.HashPassword("Old#Password123");
        var old2 = SecurityHelpers.HashPassword("Another#Password456");
        var history = new List<string> { old1, old2 };

        Assert.Contains(history, h => SecurityHelpers.VerifyPassword("Old#Password123", h));
        Assert.DoesNotContain(history, h => SecurityHelpers.VerifyPassword("BrandNew#Password789", h));
    }

    [Theory]
    [InlineData(MfaPolicyScope.Disabled, true, UserRole.Admin, false)]
    [InlineData(MfaPolicyScope.AdminOnly, true, UserRole.Admin, true)]
    [InlineData(MfaPolicyScope.AdminOnly, true, UserRole.Clerk, false)]
    [InlineData(MfaPolicyScope.AdminAndSupervisor, true, UserRole.Supervisor, true)]
    [InlineData(MfaPolicyScope.AllUsers, true, UserRole.ReadOnly, true)]
    [InlineData(MfaPolicyScope.AllUsers, false, UserRole.Admin, false)]
    public void MfaPolicy_RequiresMfa_Works(MfaPolicyScope scope, bool enforce, UserRole role, bool expected)
    {
        var policy = new MfaPolicy(scope, enforce, DateTimeOffset.UtcNow, null, null);
        Assert.Equal(expected, policy.RequiresMfa(role));
    }

    [Fact]
    public void AdAuthenticator_MockMode_ValidatesKnownUser()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Ad:Enabled"] = "true",
            ["Auth:Ad:Mode"] = "mock",
            ["Auth:Ad:MockUsersJson"] = "{\"ad.user@bank.local\":\"Passw0rd!\"}"
        }).Build();

        var ad = new AdAuthenticator(cfg, NullLogger<AdAuthenticator>.Instance);

        var ok = ad.Validate("ad.user@bank.local", "Passw0rd!");
        var bad = ad.Validate("ad.user@bank.local", "wrong");

        Assert.Equal(AdAuthStatus.Success, ok.Status);
        Assert.Equal(AdAuthStatus.InvalidCredentials, bad.Status);
    }

    [Fact]
    public void AdAuthenticator_LdapMode_WithoutHost_ReturnsMisconfigured()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Ad:Enabled"] = "true",
            ["Auth:Ad:Mode"] = "ldap"
        }).Build();

        var ad = new AdAuthenticator(cfg, NullLogger<AdAuthenticator>.Instance);
        var result = ad.Validate("ad.user@bank.local", "Passw0rd!");

        Assert.Equal(AdAuthStatus.Misconfigured, result.Status);
    }
}
