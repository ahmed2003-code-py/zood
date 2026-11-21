using Zawed.Domain;
using Zawed.Domain.Rules;
using static Zawed.Domain.Rules.FuelingRules;
using Period = Zawed.Domain.Rules.Periods.Period;

namespace Zawed.Tests.Domain;

/// <summary>
/// Golden tests for the fueling decision (Java <c>FuelingRulesTests</c>, ported from the demo's
/// <c>fueling.test.ts</c> and extended): the PRD order of checks, every boundary, and the periods in Cairo time.
/// </summary>
public sealed class FuelingRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T09:00:00Z");
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    /// <summary>A fueling that passes every check: 30 L of 92 at 19 EGP/L = 570 EGP, from 1,000 EGP.</summary>
    private static Facts Clean() =>
        new(Now, new Tag(true, null), new Vehicle(true, FuelType.GAS_92, false, 50_000), new Company(true), null, 1_900L, [],
            100_000, 30_000);

    private static Facts With(Func<Builder, Builder> change) => change(new Builder(Clean())).Build();

    private static DeclineReason Reason(Facts facts) =>
        FuelingRules.Decide(facts).DeclineReason ?? throw new Xunit.Sdk.XunitException("expected a decline");

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ACleanFuelingIsApprovedAndPricedInWholePiastres()
    {
        var decision = FuelingRules.Decide(Clean());
        Assert.True(decision.Approved);
        Assert.Equal(57_000, decision.AmountPiastres);
        Assert.True(decision.Billable);
        Assert.False(decision.TankCapacityExceeded);
    }

    [Fact]
    public void TheAmountRoundsHalfUpToTheNearestPiastre() =>
        // 12.345 L at 19.01 EGP/L = 234.67845 EGP -> 23,468 piastres.
        Assert.Equal(23_468, FuelingRules.Decide(With(b => b.Volume(12_345).Price(1_901L))).AmountPiastres);

    // ---------------------------------------------------------------------------------- order of checks

    [Fact]
    public void CooldownIsCheckedBeforeAnythingElse()
    {
        // Every other check fails too; the cooldown still wins (PRD 6.1).
        var everythingWrong = new Facts(Now, new Tag(false, Now.AddSeconds(60)), new Vehicle(false, FuelType.DIESEL, false, 50_000),
            new Company(false), FuelType.GAS_92, null, [new Limit(LimitKind.VOLUME, Period.DAY, 1, 1)], 0, 30_000);
        Assert.Equal(DeclineReason.COOLDOWN, Reason(everythingWrong));
    }

    [Fact]
    public void EachCheckWinsOverEveryLaterOne()
    {
        Assert.Equal(DeclineReason.TAG_INACTIVE, Reason(With(b => b.TagActive(false).VehicleActive(false).CompanyActive(false))));
        Assert.Equal(DeclineReason.VEHICLE_INACTIVE, Reason(With(b => b.VehicleActive(false).CompanyActive(false))));
        Assert.Equal(DeclineReason.COMPANY_FROZEN, Reason(With(b => b.CompanyActive(false).Requested(FuelType.DIESEL))));
        Assert.Equal(DeclineReason.FUEL_TYPE_MISMATCH, Reason(With(b => b.Requested(FuelType.DIESEL).Price(null))));
        Assert.Equal(DeclineReason.FUEL_NOT_AVAILABLE, Reason(With(b => b.Price(null).Balance(0))));
        Assert.Equal(DeclineReason.LIMIT_EXCEEDED, Reason(With(b => b.Limit(new Limit(LimitKind.VOLUME, Period.DAY, 10_000, 0)).Balance(0))));
        Assert.Equal(DeclineReason.INSUFFICIENT_BALANCE, Reason(With(b => b.Balance(0))));
    }

    [Fact]
    public void AnExpiredCooldownDoesNotBlock()
    {
        Assert.True(FuelingRules.Decide(With(b => b.Cooldown(Now))).Approved, "ends exactly now");
        Assert.True(FuelingRules.Decide(With(b => b.Cooldown(Now.AddSeconds(-1)))).Approved);
    }

    [Fact]
    public void SelectingTheVehiclesOwnFuelIsAllowed() =>
        Assert.True(FuelingRules.Decide(With(b => b.Requested(FuelType.GAS_92))).Approved);

    // ----------------------------------------------------------------------------------------- limits

    [Fact]
    public void ReachingAVolumeLimitExactlyIsAllowedAndOneMillilitreMoreIsNot()
    {
        // 50 L a day, 20 L used, 30 L requested: exactly 50.
        Assert.True(FuelingRules.Decide(With(b => b.Limit(new Limit(LimitKind.VOLUME, Period.DAY, 50_000, 20_000)))).Approved);
        Assert.Equal(DeclineReason.LIMIT_EXCEEDED, Reason(With(b => b.Limit(new Limit(LimitKind.VOLUME, Period.DAY, 50_000, 20_001)))));
    }

    [Fact]
    public void AnAmountLimitCountsThePriceOfThisFueling()
    {
        // 3,000 EGP a week, 2,430 EGP used, this fueling costs 570 EGP: exactly 3,000.
        Assert.True(FuelingRules.Decide(With(b => b.Limit(new Limit(LimitKind.AMOUNT, Period.WEEK, 300_000, 243_000)))).Approved);
        Assert.Equal(DeclineReason.LIMIT_EXCEEDED, Reason(With(b => b.Limit(new Limit(LimitKind.AMOUNT, Period.WEEK, 300_000, 243_001)))));
    }

    [Fact]
    public void EveryLimitMustHold() =>
        Assert.Equal(DeclineReason.LIMIT_EXCEEDED, Reason(With(b => b.Limit(new Limit(LimitKind.VOLUME, Period.DAY, 200_000, 0))
            .Limit(new Limit(LimitKind.AMOUNT, Period.MONTH, 50_000, 0)))));

    // ----------------------------------------------------------------------------------------- wallet

    [Fact]
    public void AFuelingMayLeaveTheWalletAtZeroButNotBelow()
    {
        Assert.True(FuelingRules.Decide(With(b => b.Balance(57_000))).Approved);
        Assert.Equal(DeclineReason.INSUFFICIENT_BALANCE, Reason(With(b => b.Balance(56_999))));
    }

    // -------------------------------------------------------------------------------------------- VIP

    [Fact]
    public void AVipVehicleSkipsLimitsAndWalletAndIsNotBilled()
    {
        var decision = FuelingRules.Decide(With(b => b.Vip(true).Balance(0).Limit(new Limit(LimitKind.VOLUME, Period.DAY, 1, 1))));
        Assert.True(decision.Approved);
        Assert.False(decision.Billable);
    }

    [Fact]
    public void AVipVehicleStillHonoursTheCooldownTagVehicleCompanyAndFuel()
    {
        Assert.Equal(DeclineReason.COOLDOWN, Reason(With(b => b.Vip(true).Cooldown(Now.AddSeconds(1)))));
        Assert.Equal(DeclineReason.COMPANY_FROZEN, Reason(With(b => b.Vip(true).CompanyActive(false))));
        Assert.Equal(DeclineReason.FUEL_NOT_AVAILABLE, Reason(With(b => b.Vip(true).Price(null))));
    }

    // ------------------------------------------------------------- all failures (B5: offline is never declined)

    [Fact]
    public void ACleanFuelingHasNone() => Assert.Empty(FuelingRules.AllFailures(Clean()));

    [Fact]
    public void EveryFailingCheckIsListedInTheOrderOfTheChecks()
    {
        var everythingWrong = new Facts(Now, new Tag(false, Now.AddSeconds(60)), new Vehicle(false, FuelType.DIESEL, false, 50_000),
            new Company(false), FuelType.GAS_92, 1_900L, [new Limit(LimitKind.VOLUME, Period.DAY, 1, 1)], 0, 30_000);
        Assert.Equal(
            [DeclineReason.COOLDOWN, DeclineReason.TAG_INACTIVE, DeclineReason.VEHICLE_INACTIVE, DeclineReason.COMPANY_FROZEN,
                DeclineReason.FUEL_TYPE_MISMATCH, DeclineReason.LIMIT_EXCEEDED, DeclineReason.INSUFFICIENT_BALANCE],
            FuelingRules.AllFailures(everythingWrong));
    }

    [Fact]
    public void TheFirstFailureIsTheDeclineReason()
    {
        Facts[] cases =
        [
            Clean(), With(b => b.Cooldown(Now.AddSeconds(1)).Balance(0)), With(b => b.TagActive(false).CompanyActive(false)),
            With(b => b.VehicleActive(false).Price(null)), With(b => b.CompanyActive(false).Balance(0)),
            With(b => b.Requested(FuelType.DIESEL).Balance(0)), With(b => b.Price(null).Limit(new Limit(LimitKind.VOLUME, Period.DAY, 1, 0))),
            With(b => b.Limit(new Limit(LimitKind.AMOUNT, Period.MONTH, 1, 0)).Balance(0)), With(b => b.Balance(0)),
            With(b => b.Vip(true).Balance(0)),
        ];
        foreach (var facts in cases)
        {
            var all = FuelingRules.AllFailures(facts);
            Assert.Equal(all.Count == 0 ? null : all[0], FuelingRules.Decide(facts).DeclineReason);
        }
    }

    [Fact]
    public void WithoutAPriceTheAmountIsZeroSoOnlyChecksThatNeedNoAmountCanFail()
    {
        // No price: nothing to charge, so the wallet check holds even on an empty wallet; a volume limit is still a
        // volume limit.
        Assert.Equal([DeclineReason.FUEL_NOT_AVAILABLE], FuelingRules.AllFailures(With(b => b.Price(null).Balance(0))));
        Assert.Equal([DeclineReason.FUEL_NOT_AVAILABLE, DeclineReason.LIMIT_EXCEEDED],
            FuelingRules.AllFailures(With(b => b.Price(null).Balance(0).Limit(new Limit(LimitKind.VOLUME, Period.DAY, 10_000, 0)))));
    }

    [Fact]
    public void SeveralBrokenLimitsAreOneReason() =>
        Assert.Equal([DeclineReason.LIMIT_EXCEEDED], FuelingRules.AllFailures(With(b => b.Limit(new Limit(LimitKind.VOLUME, Period.DAY, 10_000, 0))
            .Limit(new Limit(LimitKind.AMOUNT, Period.MONTH, 50_000, 0)))));

    [Fact]
    public void AVipVehicleNeverFailsOnLimitsOrWallet()
    {
        Assert.Empty(FuelingRules.AllFailures(With(b => b.Vip(true).Balance(0).Limit(new Limit(LimitKind.VOLUME, Period.DAY, 1, 1)))));
        Assert.Equal([DeclineReason.TAG_INACTIVE], FuelingRules.AllFailures(With(b => b.Vip(true).Balance(0).TagActive(false))));
    }

    // ------------------------------------------------------------------------------------------- misc

    [Fact]
    public void MoreThanTheTankHoldsIsApprovedButFlagged()
    {
        var decision = FuelingRules.Decide(With(b => b.Volume(50_001).Balance(1_000_000)));
        Assert.True(decision.Approved);
        Assert.True(decision.TankCapacityExceeded);
        Assert.False(FuelingRules.Decide(With(b => b.Volume(50_000).Balance(1_000_000))).TankCapacityExceeded);
    }

    [Fact]
    public void AVolumeMustBePositive() => Assert.Throws<ArgumentException>(() => With(b => b.Volume(0)));

    // ------------------------------------------------------------------------------- periods in Cairo

    [Fact]
    public void ADayStartsAtCairoMidnightNotUtcMidnight()
    {
        // 23:30 UTC on the 12th is already 01:30 on the 13th in Cairo (UTC+2 in November).
        var lateUtc = At("2026-11-12T23:30:00Z");
        Assert.Equal(new DateOnly(2026, 11, 13), Periods.StartDate(lateUtc, Period.DAY, Cairo));
        Assert.Equal(At("2026-11-12T22:00:00Z"), Periods.Start(lateUtc, Period.DAY, Cairo));
        Assert.False(Periods.SamePeriod(lateUtc, At("2026-11-12T21:59:59Z"), Period.DAY, Cairo));
    }

    [Fact]
    public void AWeekStartsOnSaturday()
    {
        // Friday 13 November 2026 belongs to the week that started on Saturday 7 November.
        Assert.Equal(new DateOnly(2026, 11, 7), Periods.StartDate(Now, Period.WEEK, Cairo));
        var saturday = At("2026-11-14T08:00:00Z");
        Assert.Equal(new DateOnly(2026, 11, 14), Periods.StartDate(saturday, Period.WEEK, Cairo));
        Assert.False(Periods.SamePeriod(Now, saturday, Period.WEEK, Cairo));
    }

    [Fact]
    public void AMonthStartsOnTheFirst()
    {
        Assert.Equal(new DateOnly(2026, 11, 1), Periods.StartDate(Now, Period.MONTH, Cairo));
        // 22:30 UTC on 31 October is 00:30 on 1 November in Cairo (UTC+2 after daylight saving).
        Assert.Equal(new DateOnly(2026, 11, 1), Periods.StartDate(At("2026-10-31T22:30:00Z"), Period.MONTH, Cairo));
    }

    [Fact]
    public void APeriodEndsWhereTheNextOneStarts()
    {
        // B5: an offline capture is counted in the period of its own time, which may be over.
        Assert.Equal(At("2026-11-13T22:00:00Z"), Periods.End(Now, Period.DAY, Cairo));
        Assert.Equal(At("2026-11-13T22:00:00Z"), Periods.End(Now, Period.WEEK, Cairo));
        Assert.Equal(Periods.Start(At("2026-11-14T08:00:00Z"), Period.WEEK, Cairo), Periods.End(Now, Period.WEEK, Cairo));
        Assert.Equal(At("2026-11-30T22:00:00Z"), Periods.End(Now, Period.MONTH, Cairo));
        // The day the clocks go back is 25 hours long: it still ends at the next local midnight.
        var changeDay = At("2026-10-29T10:00:00Z");
        Assert.Equal(At("2026-10-28T21:00:00Z"), Periods.Start(changeDay, Period.DAY, Cairo));
        Assert.Equal(At("2026-10-29T22:00:00Z"), Periods.End(changeDay, Period.DAY, Cairo));
        // A month that ends in a shorter one: 31 January to 1 February, not 31 days later.
        Assert.Equal(At("2027-01-31T22:00:00Z"), Periods.End(At("2027-01-31T10:00:00Z"), Period.MONTH, Cairo));
    }

    [Fact]
    public void ADayAcrossTheDaylightSavingChangeStillStartsAtLocalMidnight()
    {
        // Egypt returns to UTC+2 at the end of the last Thursday of October 2026 (29 October).
        Assert.Equal(At("2026-10-29T22:00:00Z"), Periods.Start(At("2026-10-30T10:00:00Z"), Period.DAY, Cairo));
        Assert.Equal(At("2026-10-27T21:00:00Z"), Periods.Start(At("2026-10-28T10:00:00Z"), Period.DAY, Cairo));
    }

    [Fact]
    public void ADayWhoseMidnightFallsInTheSpringGapStartsWhenTheGapEnds()
    {
        // Added in the port (no Java vector): Egypt moves to UTC+3 at 00:00 on the last Friday of April (24 April
        // 2026), so that midnight does not exist. Java's atStartOfDay gives 01:00+03:00 = 22:00Z the day before
        // (checked with jshell, 2026-11-21); the next day starts at 21:00Z.
        Assert.Equal(At("2026-04-23T22:00:00Z"), Periods.Start(At("2026-04-24T10:00:00Z"), Period.DAY, Cairo));
        Assert.Equal(At("2026-04-24T21:00:00Z"), Periods.End(At("2026-04-24T10:00:00Z"), Period.DAY, Cairo));
        Assert.Equal(new DateOnly(2026, 4, 24), Periods.StartDate(At("2026-04-23T23:30:00Z"), Period.DAY, Cairo));
    }

    /// <summary>Changes one fact at a time from the clean fueling.</summary>
    private sealed class Builder(Facts facts)
    {
        private DateTimeOffset? cooldown = facts.Tag.CooldownUntil;
        private bool tagActive = facts.Tag.Active;
        private bool vehicleActive = facts.Vehicle.Active;
        private bool vip = facts.Vehicle.Vip;
        private readonly FuelType fuel = facts.Vehicle.FuelType;
        private readonly long tank = facts.Vehicle.TankCapacityMl;
        private bool companyActive = facts.Company.Active;
        private FuelType? requested = facts.RequestedFuel;
        private long? price = facts.UnitPricePiastres;
        private readonly List<Limit> limits = [.. facts.Limits];
        private long balance = facts.WalletBalancePiastres;
        private long volume = facts.VolumeMl;

        public Builder Cooldown(DateTimeOffset? value) { cooldown = value; return this; }
        public Builder TagActive(bool value) { tagActive = value; return this; }
        public Builder VehicleActive(bool value) { vehicleActive = value; return this; }
        public Builder Vip(bool value) { vip = value; return this; }
        public Builder CompanyActive(bool value) { companyActive = value; return this; }
        public Builder Requested(FuelType? value) { requested = value; return this; }
        public Builder Price(long? value) { price = value; return this; }
        public Builder Limit(Limit value) { limits.Add(value); return this; }
        public Builder Balance(long value) { balance = value; return this; }
        public Builder Volume(long value) { volume = value; return this; }

        public Facts Build() => new(Now, new Tag(tagActive, cooldown), new Vehicle(vehicleActive, fuel, vip, tank), new Company(companyActive),
            requested, price, limits, balance, volume);
    }
}
