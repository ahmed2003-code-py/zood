using Zawed.Api.Modules.Leads;
using Zawed.Api.Platform.Text;
using LeadsService = Zawed.Api.Modules.Leads.Leads;

namespace Zawed.Tests.Modules.Leads;

/// <summary>How the demo form's text is cleaned (u1-leads-contract.md §2), and when a request is set aside (§3); Java <c>DemoRequestTextTests</c>.</summary>
public sealed class DemoRequestTextTests
{
    private static bool Mobile(string? value) => value is not null && EgyptianContact.Matches(value, DemoRequestText.EgyptianMobile);

    [Theory]
    [InlineData("01001234567", "01001234567"), InlineData("+201001234567", "01001234567"), InlineData("+20 100 123 4567", "01001234567")]
    [InlineData("0020-100-123-4567", "01001234567"), InlineData("00201001234567", "01001234567"), InlineData("201001234567", "01001234567")]
    [InlineData("1001234567", "01001234567"), InlineData("(010) 0123.4567", "01001234567"), InlineData("٠١٠٠١٢٣٤٥٦٧", "01001234567")]
    [InlineData("۰۱۲۰۱۲۳۴۵۶۷", "01201234567"), InlineData("+٢٠ ١١٥ ١٢٣ ٤٥٦٧", "01151234567"), InlineData("015 5555 5555", "01555555555")]
    [InlineData("0100 123 4567", "01001234567")]
    public void EgyptianMobilesAreNormalisedTo01XXXXXXXXX(string typed, string stored)
    {
        Assert.Equal(stored, DemoRequestText.Phone(typed));
        Assert.True(Mobile(DemoRequestText.Phone(typed)));
    }

    [Theory]
    [InlineData("01301234567"), InlineData("01401234567"), InlineData("0100123456"), InlineData("010012345678"), InlineData("0223456789")]
    [InlineData("+201301234567"), InlineData("+1 202 555 0100"), InlineData("01001234567x"), InlineData("phone"), InlineData("")]
    public void AnythingElseStaysInvalid(string typed) => Assert.False(Mobile(DemoRequestText.Phone(typed)));

    [Fact]
    public void NamesAreTrimmedAndEveryRunOfWhitespaceCollapses()
    {
        Assert.Equal("Nile Freight Co", DemoRequestText.Collapse("  Nile \t Freight\n\r\nCo  "));
        Assert.Equal("شركة النيل", DemoRequestText.Collapse("  شركة   النيل  "));
        Assert.Null(DemoRequestText.Collapse(null));
    }

    [Fact]
    public void ControlAndBidiOverrideCharactersAreNotPlainText()
    {
        Assert.True(DemoRequestText.IsPlainText("Nile Freight"));
        Assert.True(DemoRequestText.IsPlainText("شركة النيل للنقل"));
        Assert.False(DemoRequestText.IsPlainText("Nile\u0000Freight"));
        Assert.False(DemoRequestText.IsPlainText("Nile\u0007"));
        Assert.False(DemoRequestText.IsPlainText("Nile\u001B[31m"));
        Assert.False(DemoRequestText.IsPlainText("evil‮txt.exe"));
        Assert.False(DemoRequestText.IsPlainText("x⁦y"));
        // Every other invisible or unassigned character too (review of U1): zero-width, marks, BOM, soft hyphen, the Arabic
        // letter mark, word joiners, tags, private use, unassigned code points, a lone surrogate.
        foreach (var hidden in new[] { "​", "‍", "‎", "‏", "؜", "⁠", "⁤", "﻿", "­", "󠁁",
            "", "􏿽", "͸", "\uD800" })
        {
            Assert.False(DemoRequestText.IsPlainText("Nile" + hidden + "Co"), $"U+{(int)hidden[0]:X4}");
            Assert.False(DemoRequestText.IsNoteText("Nile" + hidden + "Co"));
        }
        // Long values are checked without deep recursion (a stack overflow would be a 500).
        var longText = new string('x', 200_000);
        Assert.True(DemoRequestText.IsPlainText(longText) && DemoRequestText.IsNoteText(longText));
        Assert.True(DemoRequestText.IsEmail(new string('a', 100_000) + "@b.c"));
        // The zero-width non-joiner shapes Arabic and Persian script; it stays. So does an astral character (a code point,
        // not two surrogates).
        Assert.True(DemoRequestText.IsPlainText("مي‌خواهم"));
        Assert.True(DemoRequestText.IsNoteText("مي‌خواهم"));
        Assert.True(DemoRequestText.IsPlainText("Nile \U0001F69A Co"));
        // A note keeps its line breaks, and nothing else of the kind.
        Assert.True(DemoRequestText.IsNoteText("Called.\nCall back Sunday.\r\n\tPricing"));
        Assert.False(DemoRequestText.IsPlainText("Called.\nCall back"));
        Assert.False(DemoRequestText.IsNoteText("Called\u0000"));
        Assert.False(DemoRequestText.IsNoteText("Called‮"));
    }

    [Fact]
    public void EmailsAreTrimmedAndLowerCasedAndJudgedAsTheJavaExpression()
    {
        Assert.Equal("mona.adel+fleet@example.com", DemoRequestText.EmailOf("  Mona.Adel+Fleet@Example.COM "));
        Assert.True(DemoRequestText.IsEmail("a@b.c"));
        Assert.True(DemoRequestText.IsEmail("a@b.c.d"));
        Assert.True(DemoRequestText.IsEmail("a@.b.c"));
        Assert.False(DemoRequestText.IsEmail("a@b."));
        Assert.False(DemoRequestText.IsEmail("a@.b"));
        Assert.False(DemoRequestText.IsEmail("a@localhost"));
        Assert.False(DemoRequestText.IsEmail("@b.c"));
        Assert.False(DemoRequestText.IsEmail("a@b@c.d"));
        Assert.False(DemoRequestText.IsEmail("a b@c.d"));
        Assert.False(DemoRequestText.IsEmail("a@c　.d"));
    }

    [Fact]
    public void ARequestIsSetAsideWhenTheHiddenFieldIsFilledOrTheTimingIsNotAPersons()
    {
        var now = DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(LeadsService.DropReason(Submission(null, now.AddSeconds(-10)), now));
        Assert.Null(LeadsService.DropReason(Submission("", now.AddSeconds(-10)), now));
        Assert.Null(LeadsService.DropReason(Submission("   ", now.AddSeconds(-10)), now));
        Assert.Equal("honeypot", LeadsService.DropReason(Submission("https://spam.example", now.AddSeconds(-10)), now));
        Assert.Equal("honeypot", LeadsService.DropReason(Submission(" ", now.AddSeconds(-10)), now)); // not blank to Java
        Assert.Equal("too_fast", LeadsService.DropReason(Submission(null, now.AddMilliseconds(-2_999)), now));
        Assert.Equal("too_fast", LeadsService.DropReason(Submission(null, now), now));
        Assert.Null(LeadsService.DropReason(Submission(null, now.AddSeconds(-3)), now));
        Assert.Equal("future", LeadsService.DropReason(Submission(null, now.AddSeconds(1)), now));
        Assert.Null(LeadsService.DropReason(Submission(null, now.AddSeconds(-24 * 3600)), now));
        Assert.Equal("stale", LeadsService.DropReason(Submission(null, now.AddSeconds(-24 * 3600 - 1)), now));
        Assert.Equal("stale", LeadsService.DropReason(Submission(null, DateTimeOffset.UnixEpoch), now));
    }

    private static LeadsService.Submission Submission(string? website, DateTimeOffset startedAt) =>
        new("Nile Freight", "Mona Adel", "01001234567", "mona@example.com", "20-49", "ar", website, startedAt.ToUnixTimeMilliseconds());
}
