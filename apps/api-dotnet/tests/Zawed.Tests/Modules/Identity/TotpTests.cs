using System.Text;
using Zawed.Api.Modules.Identity;

namespace Zawed.Tests.Modules.Identity;

/// <summary>RFC 6238 Appendix B test vectors (SHA-1, 8 digits) and the 6-digit form apps use (Java <c>TotpTests</c>).</summary>
public sealed class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Fact]
    public void MatchesTheRfcVectors()
    {
        Assert.Equal("94287082", Totp.CodeAt(RfcSecret, Totp.StepAt(59), 8, "HmacSHA1"));
        Assert.Equal("07081804", Totp.CodeAt(RfcSecret, Totp.StepAt(1111111109), 8, "HmacSHA1"));
        Assert.Equal("14050471", Totp.CodeAt(RfcSecret, Totp.StepAt(1111111111), 8, "HmacSHA1"));
        Assert.Equal("89005924", Totp.CodeAt(RfcSecret, Totp.StepAt(1234567890), 8, "HmacSHA1"));
        Assert.Equal("69279037", Totp.CodeAt(RfcSecret, Totp.StepAt(2000000000), 8, "HmacSHA1"));
    }

    [Fact]
    public void AcceptsOneStepOfClockDriftAndNoMore()
    {
        const long now = 1_800_000_000L;
        var step = Totp.StepAt(now);
        Assert.Equal(step, Totp.MatchingStep(RfcSecret, Totp.CodeAt(RfcSecret, step), now));
        Assert.Equal(step - 1, Totp.MatchingStep(RfcSecret, Totp.CodeAt(RfcSecret, step - 1), now));
        Assert.Equal(step + 1, Totp.MatchingStep(RfcSecret, Totp.CodeAt(RfcSecret, step + 1), now));
        Assert.Null(Totp.MatchingStep(RfcSecret, Totp.CodeAt(RfcSecret, step - 2), now));
    }

    [Fact]
    public void RefusesAnythingThatIsNotSixDigits()
    {
        Assert.Null(Totp.MatchingStep(RfcSecret, "12345", 0));
        Assert.Null(Totp.MatchingStep(RfcSecret, "abcdef", 0));
        Assert.Null(Totp.MatchingStep(RfcSecret, null, 0));
    }

    [Fact]
    public void WritesTheKeyAppsExpect()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcSecret));
        Assert.Equal(
            "otpauth://totp/ZAWED%3Aops%40zawed.test?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=ZAWED&algorithm=SHA1&digits=6&period=30",
            Totp.OtpauthUri("ZAWED", "ops@zawed.test", RfcSecret));
    }

    [Fact]
    public void TheStepIsAFloorDivisionBeforeTheEpochToo()
    {
        // Java's Math.floorDiv: -1 s is step -1, not 0.
        Assert.Equal(0, Totp.StepAt(29));
        Assert.Equal(-1, Totp.StepAt(-1));
        Assert.Equal(-1, Totp.StepAt(-30));
        Assert.Equal(-2, Totp.StepAt(-31));
    }

    [Fact]
    public void ASecretIsBoundToItsUserAndAPendingOneToItsChallengeLikeJava()
    {
        // Java: ByteBuffer.put("user").putLong(msb).putLong(lsb), big-endian: the UUID's canonical bytes.
        var id = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
        Assert.Equal("757365720193a1b2c3d47e5f8a9b0c1d2e3f4a5b", Convert.ToHexStringLower(SignIn.UserBinding(id)));
        Assert.Equal("6368616c0193a1b2c3d47e5f8a9b0c1d2e3f4a5b", Convert.ToHexStringLower(SignIn.ChallengeBinding(id)));
    }
}
