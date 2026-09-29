using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Tests;

public sealed class SettlementQueryTests
{
    private static readonly DateTimeOffset T0 = TestApp.Start;

    public static TheoryData<string> States => ["draft", "pending", "replied", "replied+captured", "replied+unpaid", "declined", "declined+canceled", "expired", "abandoned", "abandoned+canceled"];

    [Theory]
    [MemberData(nameof(States))]
    public void The_sql_query_agrees_with_the_domain_about_which_payments_need_settling(string state)
    {
        var message = Build(state);

        var matches = StampQueries.NeedsSettlement.Compile()(message);

        Assert.Equal(message.NeedsCapture || message.NeedsCancellation, matches);
    }

    private static StampedMessage Build(string state)
    {
        var message = StampedMessage.Create(Guid.NewGuid(), "Ada", "ada@example.com", "Hi", "Body", 500, 50, "usd", T0);
        message.AttachPayment("pi_1");
        var parts = state.Split('+');

        if (parts[0] is "abandoned")
        {
            message.Abandon(T0);
        }
        else if (parts[0] is not "draft")
        {
            message.MarkAuthorized(T0, TimeSpan.FromDays(6));
            switch (parts[0])
            {
                case "replied": message.Reply(new string('r', 20), T0.AddDays(1)); break;
                case "declined": message.Decline(T0.AddDays(1)); break;
                case "expired": message.Expire(T0.AddDays(6)); break;
            }
        }

        switch (parts.ElementAtOrDefault(1))
        {
            case "captured": message.MarkPaymentCaptured(); break;
            case "canceled" or "unpaid": message.MarkPaymentCanceled(T0.AddDays(2)); break;
        }

        return message;
    }
}

public sealed class EmailTemplateTests
{
    [Fact]
    public void Anything_a_user_typed_is_html_encoded()
    {
        var templates = new EmailTemplates(new TestUrls(), Options.Create(new StampOptions()));
        var receiver = Receiver.Register("kalai@example.com", TestApp.Start);
        receiver.UpdateProfile("kalai", "Kalai", null, null, 500, false, TestApp.Start);
        var message = StampedMessage.Create(receiver.Id, "<script>alert(1)</script>", "ada@example.com", "<b>Hi</b>", "<img src=x onerror=alert(1)>", 500, 50, "usd", TestApp.Start);
        message.AttachPayment("pi_1");
        message.MarkAuthorized(TestApp.Start, TimeSpan.FromDays(6));

        var html = templates.StampReceived(receiver, message).Message.HtmlBody;

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}

public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void A_reply_window_as_long_as_the_card_hold_is_refused_at_startup(int days)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
        services.AddSingleton<IAppUrls, TestUrls>();
        services.AddStampApplication();
        services.Configure<StampOptions>(o => o.ReplyWindow = TimeSpan.FromDays(days));
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StampOptions>>().Value);

        Assert.Contains("authorization hold", error.Message);
    }

    [Fact]
    public void The_default_six_day_window_is_accepted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
        services.AddStampApplication();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromDays(6), provider.GetRequiredService<IOptions<StampOptions>>().Value.ReplyWindow);
    }
}
