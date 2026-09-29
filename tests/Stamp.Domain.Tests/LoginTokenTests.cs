using Stamp.Domain.Auth;
using Stamp.Domain.Common;

namespace Stamp.Domain.Tests;

public sealed class LoginTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    [Fact]
    public void Issued_tokens_store_only_a_hash_of_a_random_token()
    {
        var (token, raw) = LoginToken.Issue(" Kalai@Example.com ", Now, Lifetime);
        var (_, otherRaw) = LoginToken.Issue("kalai@example.com", Now, Lifetime);

        Assert.Equal("kalai@example.com", token.Email);
        Assert.Equal(LoginToken.Hash(raw), token.TokenHash);
        Assert.DoesNotContain(raw, token.TokenHash, StringComparison.Ordinal);
        Assert.NotEqual(raw, otherRaw);
        Assert.True(raw.Length >= 43);
        Assert.Equal(Now + Lifetime, token.ExpiresAt);
    }

    [Fact]
    public void A_token_can_be_used_once()
    {
        var (token, _) = LoginToken.Issue("kalai@example.com", Now, Lifetime);

        token.Consume(Now.AddMinutes(1));

        Assert.False(token.IsUsable(Now.AddMinutes(2)));
        Assert.Equal(DomainErrorCodes.LoginTokenUnusable,
            Assert.Throws<DomainException>(() => token.Consume(Now.AddMinutes(2))).Code);
    }

    [Fact]
    public void A_token_expires_after_its_lifetime()
    {
        var (token, _) = LoginToken.Issue("kalai@example.com", Now, Lifetime);

        Assert.True(token.IsUsable(Now + Lifetime - TimeSpan.FromSeconds(1)));
        Assert.False(token.IsUsable(Now + Lifetime));
        Assert.Throws<DomainException>(() => token.Consume(Now + Lifetime));
    }
}
