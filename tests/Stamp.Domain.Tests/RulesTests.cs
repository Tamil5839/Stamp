using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Domain.Tests;

public sealed class StampPricingTests
{
    [Theory]
    [InlineData(200, 20)]
    [InlineData(205, 21)]   // 20.5 rounds half away from zero
    [InlineData(999, 100)]  // 99.9
    [InlineData(500, 50)]
    [InlineData(50_000, 5_000)]
    public void Platform_fee_is_ten_percent_rounded_to_the_cent(long amount, long expectedFee)
    {
        Assert.Equal(expectedFee, StampPricing.PlatformFee(amount, 10m));
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(50_000, true)]
    [InlineData(50_001, false)]
    public void Prices_must_be_between_2_and_500_dollars(long price, bool valid)
    {
        Assert.Equal(valid, StampPricing.IsValidPrice(price));
    }

    [Fact]
    public void Fee_percent_must_be_a_percentage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StampPricing.PlatformFee(500, 101m));
        Assert.Throws<ArgumentOutOfRangeException>(() => StampPricing.PlatformFee(500, -1m));
    }
}

public sealed class HandleRulesTests
{
    [Theory]
    [InlineData("kalai", "kalai")]
    [InlineData("  @Kalai_42 ", "kalai_42")]
    [InlineData("abc", "abc")]
    [InlineData("a23456789012345678901234567890", "a23456789012345678901234567890")]
    public void Valid_handles_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, HandleRules.NormalizeValid(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901")]
    [InlineData("kalai.b")]
    [InlineData("kalai-b")]
    [InlineData("kal ai")]
    [InlineData("kälai")]
    public void Malformed_handles_are_rejected(string input)
    {
        Assert.Equal(DomainErrorCodes.InvalidHandle, Assert.Throws<DomainException>(() => HandleRules.NormalizeValid(input)).Code);
    }

    [Theory]
    [InlineData("inbox")]
    [InlineData("Settings")]
    [InlineData("webhooks")]
    [InlineData("auth")]
    public void Handles_that_collide_with_app_routes_are_reserved(string input)
    {
        Assert.Equal(DomainErrorCodes.ReservedHandle, Assert.Throws<DomainException>(() => HandleRules.NormalizeValid(input)).Code);
    }
}

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("ada@example.com", true)]
    [InlineData(" Ada@Example.com ", true)]
    [InlineData("ada+stamps@mail.example.co.uk", true)]
    [InlineData("ada@example", false)]
    [InlineData("ada example@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_email_shape(string? email, bool expected)
    {
        Assert.Equal(expected, EmailAddress.IsValid(email));
    }

    [Fact]
    public void Normalizes_by_trimming_and_lowercasing()
    {
        Assert.Equal("ada@example.com", EmailAddress.NormalizeValid("  ADA@Example.Com "));
    }

    [Fact]
    public void Rejects_overlong_addresses()
    {
        var email = new string('a', 250) + "@x.io";
        Assert.False(EmailAddress.IsValid(email));
    }
}

public sealed class MoneyTests
{
    [Theory]
    [InlineData(500, "usd", "$5")]
    [InlineData(550, "usd", "$5.50")]
    [InlineData(50_000, "USD", "$500")]
    [InlineData(123_456, "usd", "$1,234.56")]
    [InlineData(1000, "inr", "₹10")]
    [InlineData(1000, "eur", "10 EUR")]
    public void Formats_minor_units(long amount, string currency, string expected)
    {
        Assert.Equal(expected, Money.Format(amount, currency));
    }
}

public sealed class TextRulesTests
{
    [Theory]
    [InlineData("  Quick\r\nquestion \t here ", "Quick question here")]
    [InlineData("Bcc: evil@example.com\nSubject", "Bcc: evil@example.com Subject")]
    [InlineData("zero\u0000width\u0007", "zerowidth")]
    [InlineData(null, "")]
    public void Single_line_collapses_whitespace_and_drops_control_characters(string? input, string expected)
    {
        Assert.Equal(expected, TextRules.SingleLine(input));
    }

    [Fact]
    public void Multi_line_normalizes_line_endings_and_keeps_paragraphs()
    {
        Assert.Equal("Hi,\n\nThanks!\tBye", TextRules.MultiLine("  Hi,\r\n\r\nThanks!\tBye\u0000  "));
    }
}
