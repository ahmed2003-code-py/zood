using Zawed.Api.Modules.Ledger;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Tests.Platform;

/// <summary>
/// The Idempotency-Key body hash is the Java one byte for byte, so a key a Java instance stored replays on a .NET
/// instance during the cutover (ADR 0022). Jackson 3 escapes only <c>"</c>, <c>\</c> and U+0000 to U+001F (with
/// <c>\b \t \n \f \r</c> and upper-case <c>\u00XX</c>), and writes everything else as it is, including DEL, the
/// no-break spaces, the line separators, the BOM and characters outside the BMP. The hashes are SHA-256 of
/// <c>writeValueAsString(List.of(company, new WalletAdjustmentRequest(2500L, reason)))</c> with jackson-databind 3.1.7's
/// default <c>JsonMapper</c> (Spring Boot 4.1.2) on JDK 25.0.5, as <c>WalletAdjustments</c> hashed it.
/// </summary>
public sealed class IdempotencyHashTests
{
    private static readonly Guid Company = Guid.Parse("019a0000-0000-7000-8000-000000000001");

    [Theory]
    [InlineData("Top\u00A0up\u2007figure\u202Fspace", "ea61a70e7512227dc3e05033dd4007a7d9b64f38dbebabc92fc2cdffb34ac1d7")]
    [InlineData("Paid \U0001F4B0 in cash \U0001F600", "46ca6c5f054f72a6b782bba6e7798e4ba4b529d417f359b88ceeeb0d17085dfe")]
    [InlineData("تعويض عن رسوم ١٢٣", "fa4324b8bfc5c1fb71718014a4232ba4843e57cfac0c143729947ee44a9f6665")]
    [InlineData("line\u2028para\u2029bom\uFEFFnel\u0085zw\u200Bend", "e5967c7797e13caeffb7c41aea7c4715e14a00410bbf60620fa0e686c8f71bef")]
    [InlineData("nul\u0000soh\u0001bs\bt\tn\nvt\u000Bff\fcr\rso\u000Eus\u001Fdel\u007F", "b35b28c64b6f27a3d4506bd3bda01209fbbdc4bfc237aaac5b95ff8354cf7277")]
    [InlineData("<b>Tom & Jerry's</b> \"quoted\" back\\slash /path/", "1e0aaa8c1eef3f8cfa2cedafa584cec77a36e11d072b900e4691c52ddcfe08b2")]
    [InlineData("café ß ı © \u00AD soft-hyphen", "f858f315d52824592fd563b76d04c68ab3d710f0c76e7c254ebc22e01e0ae429")]
    public void TheHashIsJacksons(string reason, string javaHash) =>
        Assert.Equal(javaHash, IdempotencyKeys.Hash(new object[] { Company, new WalletAdjustmentRequest(2_500, reason) }));

    [Fact]
    public void TheJsonIsJacksons()
    {
        // The text the hashes above are taken over, as Jackson wrote it for the control-character case.
        Assert.Equal("[\"019a0000-0000-7000-8000-000000000001\",{\"amountPiastres\":2500,\"reason\":"
            + "\"nul\\u0000soh\\u0001bs\\bt\\tn\\nvt\\u000Bff\\fcr\\rso\\u000Eus\\u001Fdel\u007F\"}]",
            IdempotencyKeys.Json(new object[] { Company, new WalletAdjustmentRequest(2_500, "nul\u0000soh\u0001bs\bt\tn\nvt\u000Bff\fcr\rso\u000Eus\u001Fdel\u007F") }));
        Assert.Equal("[\"019a0000-0000-7000-8000-000000000001\",{\"amountPiastres\":2500,\"reason\":null}]",
            IdempotencyKeys.Json(new object[] { Company, new WalletAdjustmentRequest(2_500, null!) }));
    }
}
