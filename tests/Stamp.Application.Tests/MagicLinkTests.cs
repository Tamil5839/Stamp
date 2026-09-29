using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Stamp.Application.Auth;
using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Common;

namespace Stamp.Application.Tests;

public sealed partial class MagicLinkTests
{
    [GeneratedRegex(@"token=([A-Za-z0-9_%\-]+)")]
    private static partial Regex TokenPattern();

    private static Task<Result> RequestAsync(TestApp app, string email) =>
        app.Run<MagicLinkService, Result>(s => s.RequestLinkAsync(email, TestApp.Ct));

    private static Task<Result<SignedInReceiver>> RedeemAsync(TestApp app, string? token) =>
        app.Run<MagicLinkService, Result<SignedInReceiver>>(s => s.RedeemAsync(token, TestApp.Ct));

    private static async Task<string> LatestTokenAsync(TestApp app)
    {
        var email = Assert.Single(await app.EmailsAsync(), e => e.Template == EmailTemplateNames.MagicLink);
        return Uri.UnescapeDataString(TokenPattern().Match(email.TextBody).Groups[1].Value);
    }

    [Fact]
    public async Task Requesting_a_link_emails_a_single_use_link_and_stores_only_its_hash()
    {
        await using var app = await TestApp.CreateAsync();

        Assert.True((await RequestAsync(app, " Kalai@Example.com ")).IsSuccess);

        var email = Assert.Single(await app.EmailsAsync());
        Assert.Equal(EmailTemplateNames.MagicLink, email.Template);
        Assert.Equal("kalai@example.com", email.To);
        Assert.Contains("expires in 15 minutes", email.TextBody);

        var token = await LatestTokenAsync(app);
        var stored = await app.Db(db => db.LoginTokens.SingleAsync(TestApp.Ct));
        Assert.NotEqual(token, stored.TokenHash);
    }

    [Fact]
    public async Task The_first_sign_in_registers_the_receiver()
    {
        await using var app = await TestApp.CreateAsync();
        await RequestAsync(app, "kalai@example.com");

        var result = await RedeemAsync(app, await LatestTokenAsync(app));

        Assert.Equal("kalai@example.com", result.Value.Email);
        Assert.False(result.Value.HasProfile);
        Assert.Equal(1, await app.Db(db => db.Receivers.CountAsync(TestApp.Ct)));
    }

    [Fact]
    public async Task Returning_receivers_sign_into_their_existing_account()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(handle: "kalai");
        await RequestAsync(app, receiver.Email.ToUpperInvariant());

        var result = await RedeemAsync(app, await LatestTokenAsync(app));

        Assert.Equal(receiver.Id, result.Value.ReceiverId);
        Assert.True(result.Value.HasProfile);
    }

    [Fact]
    public async Task A_link_works_only_once()
    {
        await using var app = await TestApp.CreateAsync();
        await RequestAsync(app, "kalai@example.com");
        var token = await LatestTokenAsync(app);

        Assert.True((await RedeemAsync(app, token)).IsSuccess);
        Assert.Equal(ErrorCodes.LoginLinkInvalid, (await RedeemAsync(app, token)).Error?.Code);
    }

    [Fact]
    public async Task Links_expire_after_15_minutes()
    {
        await using var app = await TestApp.CreateAsync();
        await RequestAsync(app, "kalai@example.com");
        var token = await LatestTokenAsync(app);

        app.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(await app.Run<MagicLinkService, bool>(s => s.IsUsableAsync(token, TestApp.Ct)));

        app.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await app.Run<MagicLinkService, bool>(s => s.IsUsableAsync(token, TestApp.Ct)));
        Assert.Equal(ErrorCodes.LoginLinkInvalid, (await RedeemAsync(app, token)).Error?.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task Made_up_tokens_are_rejected(string? token)
    {
        await using var app = await TestApp.CreateAsync();

        Assert.Equal(ErrorCodes.LoginLinkInvalid, (await RedeemAsync(app, token)).Error?.Code);
    }

    [Fact]
    public async Task Malformed_addresses_are_rejected()
    {
        await using var app = await TestApp.CreateAsync();

        Assert.Equal(DomainErrorCodes.InvalidEmail, (await RequestAsync(app, "kalai@")).Error?.Code);
        Assert.Empty(await app.EmailsAsync());
    }

    [Fact]
    public async Task Link_requests_are_throttled_per_address_without_saying_so()
    {
        await using var app = await TestApp.CreateAsync(configureAuth: auth => auth.MagicLinksPerEmailPerHour = 3);

        for (var i = 0; i < 4; i++)
        {
            Assert.True((await RequestAsync(app, "kalai@example.com")).IsSuccess);
        }

        Assert.Equal(3, (await app.EmailsAsync()).Count);

        app.Time.Advance(TimeSpan.FromHours(1));
        await RequestAsync(app, "kalai@example.com");
        Assert.Equal(4, (await app.EmailsAsync()).Count);
    }
}
