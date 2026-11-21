namespace Zawed.Domain.Rules;

/// <summary>
/// The fueling decision (PRD 6.1), as a pure function (Java <c>rules.FuelingRules</c>): the engine gathers the facts
/// under its locks, this decides. No ASP.NET Core, no database, no clock: everything arrives as input, so the same
/// rules run on the server online (B4), at offline ingest (B5) and can be checked by golden tests.
/// <para>Ported from the demo's <c>checkFueling</c> in <c>fueling.ts</c>; the order of checks is the PRD's and is
/// part of the contract: the tag's cooldown first, then tag, vehicle, company, fuel, limits, wallet. The first
/// failing check is the reason given.</para>
/// </summary>
public static class FuelingRules
{
    /// <summary>Why a fueling is declined. Order of declaration = order of checks.</summary>
    public enum DeclineReason
    {
        COOLDOWN,
        TAG_INACTIVE,
        VEHICLE_INACTIVE,
        COMPANY_FROZEN,
        FUEL_TYPE_MISMATCH,
        FUEL_NOT_AVAILABLE,
        LIMIT_EXCEEDED,
        INSUFFICIENT_BALANCE,
    }

    /// <summary>What a limit counts.</summary>
    public enum LimitKind
    {
        /// <summary>Millilitres in the period.</summary>
        VOLUME,
        /// <summary>Piastres in the period.</summary>
        AMOUNT,
    }

    public sealed record Tag(bool Active, DateTimeOffset? CooldownUntil);

    public sealed record Vehicle(bool Active, FuelType FuelType, bool Vip, long TankCapacityMl);

    public sealed record Company(bool Active);

    /// <summary>
    /// A limit with what the vehicle has already used in the period that contains the evaluation time (completed
    /// fuelings only), in the limit's unit.
    /// </summary>
    public sealed record Limit(LimitKind Kind, Periods.Period Period, long MaxValue, long Used);

    /// <summary>Everything the decision needs.</summary>
    public sealed record Facts
    {
        /// <param name="requestedFuel">the fuel the agent selected, or null when the vehicle's fuel is implied</param>
        /// <param name="unitPricePiastres">the price per litre in force at the station, or null when the station does
        /// not sell the vehicle's fuel</param>
        /// <param name="walletBalancePiastres">read under the wallet's row lock</param>
        public Facts(DateTimeOffset evaluatedAt, Tag tag, Vehicle vehicle, Company company, FuelType? requestedFuel,
            long? unitPricePiastres, IEnumerable<Limit> limits, long walletBalancePiastres, long volumeMl)
        {
            ArgumentNullException.ThrowIfNull(tag);
            ArgumentNullException.ThrowIfNull(vehicle);
            ArgumentNullException.ThrowIfNull(company);
            ArgumentNullException.ThrowIfNull(limits);
            if (volumeMl <= 0)
            {
                throw new ArgumentException($"A fueling's volume must be positive: {volumeMl}", nameof(volumeMl));
            }
            EvaluatedAt = evaluatedAt;
            Tag = tag;
            Vehicle = vehicle;
            Company = company;
            RequestedFuel = requestedFuel;
            UnitPricePiastres = unitPricePiastres;
            Limits = limits.ToList();
            WalletBalancePiastres = walletBalancePiastres;
            VolumeMl = volumeMl;
        }

        public DateTimeOffset EvaluatedAt { get; }
        public Tag Tag { get; }
        public Vehicle Vehicle { get; }
        public Company Company { get; }
        public FuelType? RequestedFuel { get; }
        public long? UnitPricePiastres { get; }
        public IReadOnlyList<Limit> Limits { get; }
        public long WalletBalancePiastres { get; }
        public long VolumeMl { get; }
    }

    /// <summary>The outcome.</summary>
    /// <param name="DeclineReason">null when approved</param>
    /// <param name="AmountPiastres">the price of the volume (0 when the fuel is not sold); charged only when approved
    /// and billable</param>
    /// <param name="Billable">VIP fuelings are recorded but not charged (PRD 4.4)</param>
    /// <param name="TankCapacityExceeded">more than the tank holds: approved, but flagged to Operations (PRD 10)</param>
    public sealed record Decision(DeclineReason? DeclineReason, long AmountPiastres, long UnitPricePiastres, bool Billable,
        bool TankCapacityExceeded)
    {
        public bool Approved => DeclineReason is null;
    }

    public static Decision Decide(Facts facts)
    {
        var price = facts.UnitPricePiastres ?? 0;
        var amount = AmountOf(facts);
        var billable = !facts.Vehicle.Vip;
        var overTank = facts.VolumeMl > facts.Vehicle.TankCapacityMl;
        var failures = Failures(facts, amount);
        return new Decision(failures.Count == 0 ? null : failures[0], amount, price, billable, overTank);
    }

    /// <summary>
    /// Every check that fails, in the order of the checks; the first is <see cref="Decision.DeclineReason"/>. Offline
    /// ingest (B5) records them all as conflicts: the fueling has already happened at the pump, so nothing is declined
    /// and the first reason alone would hide the others.
    /// <para>Each check is judged on its own. Without a price the amount is zero: the wallet check then holds, and an
    /// amount limit fails only if it was already exceeded before this fueling. Limits count once however many are
    /// broken.</para>
    /// </summary>
    public static IReadOnlyList<DeclineReason> AllFailures(Facts facts) => Failures(facts, AmountOf(facts));

    private static long AmountOf(Facts facts) =>
        facts.UnitPricePiastres is null or 0 ? 0 : MoneyMath.AmountForVolume(facts.VolumeMl, facts.UnitPricePiastres.Value);

    /// <summary>The one implementation of the checks, in PRD order; <see cref="Decide"/> takes the first.</summary>
    private static List<DeclineReason> Failures(Facts facts, long amount)
    {
        var failures = new List<DeclineReason>();
        // PRD 6.1: the tag's cooldown is checked first, before any other validation.
        if (facts.Tag.CooldownUntil is { } cooldownUntil && cooldownUntil > facts.EvaluatedAt)
        {
            failures.Add(DeclineReason.COOLDOWN);
        }
        if (!facts.Tag.Active)
        {
            failures.Add(DeclineReason.TAG_INACTIVE);
        }
        if (!facts.Vehicle.Active)
        {
            failures.Add(DeclineReason.VEHICLE_INACTIVE);
        }
        if (!facts.Company.Active)
        {
            failures.Add(DeclineReason.COMPANY_FROZEN);
        }
        if (facts.RequestedFuel is { } selected && selected != facts.Vehicle.FuelType)
        {
            failures.Add(DeclineReason.FUEL_TYPE_MISMATCH);
        }
        if (facts.UnitPricePiastres is null)
        {
            failures.Add(DeclineReason.FUEL_NOT_AVAILABLE);
        }
        // A VIP vehicle skips the limits and the wallet (PRD 4.4); its monthly ceiling only flags.
        if (facts.Vehicle.Vip)
        {
            return failures;
        }
        foreach (var limit in facts.Limits)
        {
            var requested = limit.Kind == LimitKind.VOLUME ? facts.VolumeMl : amount;
            if (checked(limit.Used + requested) > limit.MaxValue)
            {
                failures.Add(DeclineReason.LIMIT_EXCEEDED);
                break;
            }
        }
        if (facts.WalletBalancePiastres - amount < 0)
        {
            failures.Add(DeclineReason.INSUFFICIENT_BALANCE);
        }
        return failures;
    }
}
