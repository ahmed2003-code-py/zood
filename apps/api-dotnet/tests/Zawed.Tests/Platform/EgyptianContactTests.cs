using Zawed.Api.Platform.Text;
using Zawed.Api.Platform.Web;

namespace Zawed.Tests.Platform;

/// <summary>
/// Java <c>EgyptianContactTests</c>: Egyptian phone numbers, tax registration numbers and commercial registrations
/// (QA round 1). The spellings people type are normalised to one stored form, and validation judges that form.
/// Arabic-Indic digits are written as escapes (A = U+0660 ...), so the file stays ASCII.
/// </summary>
public sealed class EgyptianContactTests
{
    /// <summary>ASCII digits to Arabic-Indic ones (U+0660 to U+0669).</summary>
    private static string Arabic(string digits) => new(digits.Select(c => c is >= '0' and <= '9' ? (char)(0x0660 + (c - '0')) : c).ToArray());

    /// <summary>ASCII digits to Persian ones (U+06F0 to U+06F9).</summary>
    private static string Persian(string digits) => new(digits.Select(c => c is >= '0' and <= '9' ? (char)(0x06F0 + (c - '0')) : c).ToArray());

    public static TheoryData<string, string> Mobiles => new()
    {
        { "01001234567", "01001234567" }, { "+20 100 123 4567", "01001234567" }, { "0020-100-123-4567", "01001234567" },
        { "201001234567", "01001234567" }, { Arabic("01001234567"), "01001234567" }, { Persian("01001234567"), "01001234567" },
        { "015 5555 5555", "01555555555" },
    };

    [Theory, MemberData(nameof(Mobiles))]
    public void MobilesAreNormalisedAndAccepted(string typed, string stored)
    {
        var phone = EgyptianContact.PhoneOf(typed)!;
        Assert.Equal(stored, phone);
        Assert.True(EgyptianContact.Matches(phone, EgyptianContact.Mobile));
        Assert.True(EgyptianContact.Matches(phone, EgyptianContact.Phone));
    }

    public static TheoryData<string, string> Landlines => new()
    {
        { "02 2345 6789", "0223456789" }, { "(02) 2345-6789", "0223456789" }, { "+20 2 2345 6789", "0223456789" },
        { "0020 2 23456789", "0223456789" }, { "03 456 7890", "034567890" }, { "+20 3 4567890", "034567890" },
        { "040 333 4444", "0403334444" }, { "088 123 4567", "0881234567" }, { "013 222 3333", "0132223333" },
        { Arabic("0223456789"), "0223456789" },
    };

    [Theory, MemberData(nameof(Landlines))]
    public void LandlinesAreNormalisedAndAcceptedAsAPhoneButNotAsAMobile(string typed, string stored)
    {
        var phone = EgyptianContact.PhoneOf(typed)!;
        Assert.Equal(stored, phone);
        Assert.True(EgyptianContact.Matches(phone, EgyptianContact.Phone));
        Assert.False(EgyptianContact.Matches(phone, EgyptianContact.Mobile));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("0100123456")]
    [InlineData("010012345678")]
    [InlineData("01301234567")]
    [InlineData("022345678")]
    [InlineData("02234567890")]
    [InlineData("0345678901")]
    [InlineData("+1 202 555 0100")]
    [InlineData("call me")]
    [InlineData("0223456789x")]
    public void AnythingElseIsRefused(string typed) =>
        Assert.False(EgyptianContact.Matches(EgyptianContact.PhoneOf(typed)!, EgyptianContact.Phone));

    [Fact]
    public void ARuleMatchesTheWholeValueAsJavasMatchesDoes()
    {
        // .NET's "$" also matches before a final newline; Java's String.matches never does.
        Assert.False(EgyptianContact.Matches("01001234567\n", EgyptianContact.Mobile));
        Assert.False(EgyptianContact.Matches("123456789\n", EgyptianContact.TaxIdRule));
    }

    public static TheoryData<string, string> TaxIds => new()
    {
        { "123456789", "123456789" }, { "123-456-789", "123456789" }, { " 123 456 789 ", "123456789" },
        { Arabic("123-456-789"), "123456789" },
    };

    [Theory, MemberData(nameof(TaxIds))]
    public void TaxIdsAreNineDigits(string typed, string stored)
    {
        Assert.Equal(stored, EgyptianContact.TaxIdOf(typed));
        Assert.True(EgyptianContact.Matches(stored, EgyptianContact.TaxIdRule));
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("1234567890")]
    [InlineData("TAX-12345")]
    [InlineData("123-456-78a")]
    public void OtherTaxIdsAreRefused(string typed) =>
        Assert.False(EgyptianContact.Matches(EgyptianContact.TaxIdOf(typed)!, EgyptianContact.TaxIdRule));

    public static TheoryData<string, string> Registrations => new()
    {
        { "12345", "12345" }, { Arabic("123456"), "123456" }, { "123 456", "123456" }, { "2020-98765", "202098765" },
    };

    [Theory, MemberData(nameof(Registrations))]
    public void CommercialRegistrationsAreDigits(string typed, string stored)
    {
        Assert.Equal(stored, EgyptianContact.CommercialRegistrationOf(typed));
        Assert.True(EgyptianContact.Matches(stored, EgyptianContact.CommercialRegistrationRule));
    }

    [Theory]
    [InlineData("CR-ABC")]
    [InlineData("12")]
    [InlineData("1234567890123")]
    public void OtherCommercialRegistrationsAreRefused(string typed) =>
        Assert.False(EgyptianContact.Matches(EgyptianContact.CommercialRegistrationOf(typed)!, EgyptianContact.CommercialRegistrationRule));

    [Fact]
    public void NullStaysNullAndAnOptionalBlankBecomesNull()
    {
        Assert.Null(EgyptianContact.PhoneOf(null));
        Assert.Null(EgyptianContact.TaxIdOf(null));
        Assert.Null(EgyptianContact.CommercialRegistrationOf(null));
        Assert.Equal("", EgyptianContact.PhoneOf("   "));
        Assert.Null(EgyptianContact.OrNull(EgyptianContact.PhoneOf("   ")));
        Assert.Null(EgyptianContact.OrNull(EgyptianContact.TaxIdOf(" - ")));
        Assert.Equal("0223456789", EgyptianContact.OrNull("0223456789"));
    }

    [Fact]
    public void BlankMeansWhatJavaCallsBlank()
    {
        // String.isBlank: Character.isWhitespace, which does not count the no-break spaces and does count U+001C.
        Assert.Equal("" + (char)0x00A0, EgyptianContact.OrNull("" + (char)0x00A0));
        Assert.Null(EgyptianContact.OrNull("" + (char)0x001C + (char)0x2003));
    }

    [Fact]
    public void ACheckKeepsAStoredValueCleansANewOneAndNamesEveryFieldThatFails()
    {
        var check = new EgyptianContact.Check("company.");
        Assert.Equal("legacy-phone", check.Phone("phone", " legacy-phone ", "legacy-phone"));
        Assert.Equal("01001234567", check.Mobile("mobile", "+20 100 123 4567", null));
        Assert.Null(check.TaxId("taxId", "   ", "123456789"));
        Assert.Null(check.CommercialRegistration("commercialRegistration", "CR-ABC", null));
        Assert.Null(check.Phone("fax", "123", null));

        var failure = Assert.Throws<FieldsInvalidException>(check.OrThrow);
        Assert.Equal(["company.commercialRegistration", "company.fax"], failure.Fields.Select(f => f.Field));
        Assert.All(failure.Fields, f => Assert.Equal("PATTERN", f.Code));
        Assert.Equal("must match \"" + EgyptianContact.Phone + "\"", failure.Fields[1].Message);
    }
}
