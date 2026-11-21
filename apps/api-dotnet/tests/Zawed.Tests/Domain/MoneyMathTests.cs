using Zawed.Domain;

namespace Zawed.Tests.Domain;

/// <summary>
/// The same vectors as <c>apps/web/src/domain/money.test.ts</c> and the Java <c>MoneyMathTests</c>. A change here that
/// is not also made there (or the reverse) means the POS, the web and the server would disagree on an amount.
/// </summary>
public sealed class MoneyMathTests
{
    [Fact]
    public void DivRoundHalfUpRoundsHalfAwayFromZero()
    {
        Assert.Equal(1, MoneyMath.DivRoundHalfUp(5, 10));
        Assert.Equal(0, MoneyMath.DivRoundHalfUp(4, 10));
        Assert.Equal(-1, MoneyMath.DivRoundHalfUp(-5, 10));
        Assert.Equal(2, MoneyMath.DivRoundHalfUp(15, 10));
    }

    [Fact]
    public void DivRoundHalfUpRejectsANonPositiveDenominator()
    {
        Assert.Throws<ArgumentException>(() => MoneyMath.DivRoundHalfUp(10, 0));
        Assert.Throws<ArgumentException>(() => MoneyMath.DivRoundHalfUp(10, -1));
    }

    [Fact]
    public void DivRoundHalfUpFailsInsteadOfOverflowing()
    {
        Assert.ThrowsAny<ArithmeticException>(() => MoneyMath.DivRoundHalfUp(long.MaxValue, 10));
        Assert.ThrowsAny<ArithmeticException>(() => MoneyMath.DivRoundHalfUp(long.MinValue, 10));
    }

    [Fact]
    public void Computes40LitresOfGasoline92At19PoundsPerLitreAs760Pounds() =>
        Assert.Equal(76_000, MoneyMath.AmountForVolume(40_000, 1900));

    [Fact]
    public void RoundsPartialPiastresHalfUp() =>
        // 42.345 L x 17.50 = 741.0375 EGP -> 741.04
        Assert.Equal(74_104, MoneyMath.AmountForVolume(42_345, 1750));

    [Fact]
    public void ConvertsAnAmountBackToVolume()
    {
        Assert.Equal(40_000, MoneyMath.VolumeForAmount(76_000, 1900));
        // 500 EGP at 19.00 = 26.315789... L -> 26.316 L
        Assert.Equal(26_316, MoneyMath.VolumeForAmount(50_000, 1900));
    }

    [Fact]
    public void FuelAmountsFailInsteadOfOverflowing() =>
        Assert.ThrowsAny<ArithmeticException>(() => MoneyMath.AmountForVolume(long.MaxValue, 1900));

    [Fact]
    public void ParsesUserInputIntoMinorUnits()
    {
        Assert.Equal(42_500, MoneyMath.ParseDecimal("42.5", 3));
        Assert.Equal(120_075, MoneyMath.ParseDecimal("1,200.75", 2));
        Assert.Equal(40_000, MoneyMath.ParseDecimal("40", 3));
        Assert.Equal(700, MoneyMath.ParseDecimal("7.", 2));
    }

    [Fact]
    public void RejectsInvalidInputAndExcessDecimals()
    {
        Assert.Null(MoneyMath.ParseDecimal("abc", 2));
        Assert.Null(MoneyMath.ParseDecimal("1.234", 2));
        Assert.Null(MoneyMath.ParseDecimal("-5", 2));
    }

    [Fact]
    public void RejectsWhatTheWebRejectsToo()
    {
        Assert.Null(MoneyMath.ParseDecimal("", 2));
        Assert.Null(MoneyMath.ParseDecimal(".5", 2));
        Assert.Null(MoneyMath.ParseDecimal(Chars(0x0664, 0x0662), 2)); // Arabic-Indic digits are not accepted
        Assert.Null(MoneyMath.ParseDecimal("90071992547409.92", 2)); // beyond the safe range
        Assert.Null(MoneyMath.ParseDecimal(null, 2));
        Assert.Equal(900_719_925_474_099, MoneyMath.ParseDecimal("9007199254740.99", 2)); // inside it
        Assert.Null(MoneyMath.ParseDecimal("1.2.3", 2));
        Assert.Equal(0, MoneyMath.ParseDecimal("0.00", 2));
    }

    [Fact]
    public void TreatsSpacesExactlyAsTheWebDoes()
    {
        Assert.Equal(120_050, MoneyMath.ParseDecimal("1" + (char)0x00A0 + "200.5", 2)); // no-break space
        Assert.Equal(120_000, MoneyMath.ParseDecimal("1" + (char)0x202F + "200", 2)); // narrow no-break space
        Assert.Equal(4_200, MoneyMath.ParseDecimal("42" + (char)0xFEFF, 2)); // byte-order mark
        Assert.Null(MoneyMath.ParseDecimal("4" + (char)0x0085 + "2", 2)); // NEL is not white space in JavaScript
        Assert.Equal(4_200, MoneyMath.ParseDecimal("4" + (char)0x2028 + "2", 2)); // a line separator is
    }

    [Fact]
    public void ConvertsWholeUnits()
    {
        Assert.Equal(842_300, MoneyMath.PoundsToPiastres(8_423));
        Assert.Equal(62_000, MoneyMath.LitresToMl(62));
    }

    private static string Chars(params int[] codes) => new(codes.Select(c => (char)c).ToArray());
}

/// <summary>Java <c>MoneyAndVolumeTests</c>.</summary>
public sealed class MoneyAndVolumeTests
{
    [Fact]
    public void MoneyArithmeticIsExact()
    {
        var wallet = Money.OfPounds(8_423).Plus(Money.OfPiastres(50)); // 842,350 piastres
        var afterFueling = wallet.Minus(Money.OfPiastres(76_000));

        Assert.Equal(Money.OfPiastres(766_350), afterFueling);
        Assert.True(afterFueling.Negate().IsNegative);
        Assert.True(Money.Zero.IsZero);
        Assert.True(Money.OfPiastres(1) > Money.Zero);
    }

    [Fact]
    public void MoneyMayBeNegative() =>
        Assert.Equal(Money.OfPiastres(-150), Money.OfPiastres(100).Minus(Money.OfPiastres(250)));

    [Fact]
    public void MoneyRejectsValuesAJavaScriptClientCannotRepresent()
    {
        Assert.ThrowsAny<ArithmeticException>(() => Money.OfPiastres(MoneyMath.MaxSafe + 1));
        Assert.ThrowsAny<ArithmeticException>(() => Money.OfPiastres(MoneyMath.MaxSafe).Plus(Money.OfPiastres(1)));
    }

    [Fact]
    public void VolumeIsNeverNegative()
    {
        Assert.Throws<ArgumentException>(() => Volume.OfMillilitres(-1));
        Assert.Equal(Volume.OfMillilitres(40_500), Volume.OfLitres(40).Plus(Volume.OfMillilitres(500)));
    }

    [Fact]
    public void PricesAVolume()
    {
        var pricePerLitre = Money.OfPiastres(1900);

        Assert.Equal(Money.OfPounds(760), Volume.OfLitres(40).AmountAt(pricePerLitre));
        Assert.Equal(Money.OfPiastres(74_104), Volume.OfMillilitres(42_345).AmountAt(Money.OfPiastres(1750)));
        Assert.Equal(Volume.OfMillilitres(26_316), Volume.BoughtWith(Money.OfPounds(500), pricePerLitre));
    }

    [Fact]
    public void RefusesToPriceWithANonPositiveUnitPrice()
    {
        Assert.Throws<ArgumentException>(() => Volume.BoughtWith(Money.OfPounds(500), Money.Zero));
        Assert.Throws<ArgumentException>(() => Volume.OfLitres(1).AmountAt(Money.OfPiastres(-1)));
    }
}
