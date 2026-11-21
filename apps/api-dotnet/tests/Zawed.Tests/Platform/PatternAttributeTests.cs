using System.ComponentModel.DataAnnotations;
using Zawed.Api.Platform.Web;

namespace Zawed.Tests.Platform;

/// <summary>
/// A <c>[Pattern]</c> whose expression has braces (a quantifier such as <c>{8}</c>) must still answer a validation
/// failure, not a 500: the message passes through <c>string.Format</c>.
/// </summary>
public class PatternAttributeTests
{
    private sealed record Body([property: Pattern("01[0125][0-9]{8}")] string? Phone);

    [Theory]
    [InlineData("01012345678", true)]
    [InlineData("0101234", false)]
    [InlineData(null, true)]
    public void ExpressionsWithBracesValidateAndDescribeTheirFailure(string? phone, bool valid)
    {
        var body = new Body(phone);
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(body, new ValidationContext(body), results, validateAllProperties: true);

        Assert.Equal(valid, ok);
        if (!valid)
        {
            var message = Assert.Single(results).ErrorMessage;
            Assert.Equal("PATTERN|must match \"01[0125][0-9]{8}\"", message);
        }
    }
}
