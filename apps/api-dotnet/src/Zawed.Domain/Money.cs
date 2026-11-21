namespace Zawed.Domain;

/// <summary>
/// An amount of money in integer piastres (Java <c>platform.money.Money</c>). It may be negative: a debit, or a wallet
/// pushed below zero by an offline capture (PRD §4.7). Arithmetic is exact and fails on overflow.
/// </summary>
public sealed record Money : IComparable<Money>
{
    public static readonly Money Zero = new(0);

    public Money(long piastres) => Piastres = MoneyMath.RequireSafe(piastres, "piastres");

    public long Piastres { get; }

    public static Money OfPiastres(long piastres) => new(piastres);

    public static Money OfPounds(long pounds) => new(MoneyMath.PoundsToPiastres(pounds));

    public Money Plus(Money other) => new(checked(Piastres + other.Piastres));

    public Money Minus(Money other) => new(checked(Piastres - other.Piastres));

    public Money Negate() => new(checked(-Piastres));

    public bool IsNegative => Piastres < 0;

    public bool IsZero => Piastres == 0;

    public bool IsPositive => Piastres > 0;

    public int CompareTo(Money? other) => other is null ? 1 : Piastres.CompareTo(other.Piastres);

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;
    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;
    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;
}

/// <summary>A fuel volume in integer millilitres (Java <c>platform.money.Volume</c>). Never negative.</summary>
public sealed record Volume : IComparable<Volume>
{
    public static readonly Volume Zero = new(0);

    public Volume(long millilitres)
    {
        if (millilitres < 0)
        {
            throw new ArgumentException($"volume cannot be negative: {millilitres}", nameof(millilitres));
        }
        Millilitres = MoneyMath.RequireSafe(millilitres, "millilitres");
    }

    public long Millilitres { get; }

    public static Volume OfMillilitres(long millilitres) => new(millilitres);

    public static Volume OfLitres(long litres) => new(MoneyMath.LitresToMl(litres));

    public Volume Plus(Volume other) => new(checked(Millilitres + other.Millilitres));

    public bool IsZero => Millilitres == 0;

    /// <summary>Amount for this volume at a unit price per litre, rounded half-up to the piastre.</summary>
    public Money AmountAt(Money unitPricePerLitre)
    {
        if (unitPricePerLitre.IsNegative)
        {
            throw new ArgumentException("unit price cannot be negative", nameof(unitPricePerLitre));
        }
        return new Money(MoneyMath.AmountForVolume(Millilitres, unitPricePerLitre.Piastres));
    }

    /// <summary>Volume that an amount buys at a unit price per litre, rounded half-up to the millilitre.</summary>
    public static Volume BoughtWith(Money amount, Money unitPricePerLitre)
    {
        if (amount.IsNegative)
        {
            throw new ArgumentException("amount cannot be negative", nameof(amount));
        }
        if (!unitPricePerLitre.IsPositive)
        {
            throw new ArgumentException("unit price must be positive", nameof(unitPricePerLitre));
        }
        return new Volume(MoneyMath.VolumeForAmount(amount.Piastres, unitPricePerLitre.Piastres));
    }

    public int CompareTo(Volume? other) => other is null ? 1 : Millilitres.CompareTo(other.Millilitres);
}
