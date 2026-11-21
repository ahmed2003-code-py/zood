using Zawed.Api.Modules.Fleet.Contracts;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Modules.Pricing.Contracts;
using Zawed.Api.Modules.Tenancy.Contracts;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Domain;
using Zawed.Domain.Rules;

namespace Zawed.Api.Modules.Fueling;

/// <summary>What the agent may be told about the wallet (never the amount). Java <c>FuelingEngine.WalletStatus</c>.</summary>
public enum WalletStatus
{
    AVAILABLE,
    LOW,
    INSUFFICIENT,
    SUSPENDED,
}

/// <summary>An online fueling as the POS asks for it (Java <c>FuelingEngine.Request</c>).</summary>
public sealed record FuelingRequest(Guid ClientTxnId, string TagUid, long VolumeMl, FuelType? FuelType, DateTimeOffset DeviceAt,
    string ReceiptLocale);

/// <summary>The engine's answer (Java <c>FuelingEngine.Result</c>).</summary>
/// <param name="WalletStatus">null on a replay, which does not read the wallet again</param>
/// <param name="CooldownUntil">null on a replay</param>
public sealed record FuelingOutcome(Fueling Fueling, bool Replayed, WalletStatus? WalletStatus, DateTimeOffset? CooldownUntil);

/// <summary>
/// The fueling engine (plan §3.4, B4 contract §3, ADR 0018; Java <c>FuelingEngine</c>, the online half). One transaction
/// per fueling:
/// <list type="number">
/// <item>A <c>clientTxnId</c> seen before returns the stored outcome; nothing is decided twice.</item>
/// <item>The tag's company wallet is locked, then the vehicle (shared) and the tag (always this order, ADR 0004, ADR 0018).
/// Every fueling of a company is therefore serialised: limits and balance cannot be raced.</item>
/// <item>The facts are read under those locks and <see cref="FuelingRules"/> judges them, in PRD §6.1 order.</item>
/// <item>The ledger posting, the fueling row (never updated), the tag's cooldown and any hard alerts are written in the
/// same transaction.</item>
/// </list>
/// The balance is checked before anything is written, so the posting that follows never refuses; a failing check declines
/// the fueling and moves no money.
/// <para>A POS has no user. The engine runs in the platform scope (<see cref="DbScopeContext.Escalate"/>), entered only here
/// and only for a device and agent the POS chain has just verified (B4 contract §1); what it writes is bounded by those:
/// the station is the device's, the company is the tag's. Each attempt runs in a transaction of its own, with the module
/// contracts resolved in a DI scope bound to it, and is retried once as a deadlock victim (the attempt rolled back
/// entirely).</para>
/// </summary>
public sealed class FuelingEngine(ITransactions transactions, IServiceScopeFactory scopes)
{
    /// <summary>A tanker truck with two tanks is under 1,500 L; anything larger is a typing error.</summary>
    public const long MaxVolumeMl = 1_500_000;

    // -------------------------------------------------------------------------------------------------- online

    public async Task<FuelingOutcome> FuelAsync(PosPrincipal pos, FuelingRequest request, CancellationToken ct)
    {
        Validate(pos, request);
        return await OnceAsync(pos, request.ClientTxnId, request.TagUid, request.VolumeMl, (step, c) => step.DecideAsync(pos, request, c), ct);
    }

    /// <summary>
    /// What became of an online fueling whose answer the terminal never received (B5 review 6). The stored fueling of this
    /// device's <c>clientTxnId</c>, or 404 when the server never got it. When the server completed it and the agent was told
    /// it was refused, the company was charged for a fueling nobody thinks happened: Operations and the company are alerted,
    /// once.
    /// </summary>
    public Task<Fueling> ResolveAsync(PosPrincipal pos, Guid clientTxnId, bool toldRefused, CancellationToken ct) =>
        AsPlatformAsync((step, c) => step.ResolveAsync(pos, clientTxnId, toldRefused, c), retryOnConflict: false, ct);

    /// <summary>
    /// Runs one attempt in its own transaction and answers a concurrent duplicate from the stored row. The attempt is tried
    /// once more if PostgreSQL chose it as a deadlock victim: the lock order makes that unlikely (ADR 0018), and the retry is
    /// safe because the attempt rolled back entirely.
    /// </summary>
    private async Task<FuelingOutcome> OnceAsync(PosPrincipal pos, Guid clientTxnId, string tagUid, long volumeMl,
        Func<Step, CancellationToken, Task<FuelingOutcome>> attempt, CancellationToken ct)
    {
        try
        {
            return await AsPlatformAsync(attempt, retryOnConflict: true, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // A concurrent request with the same clientTxnId committed first: this attempt rolled back entirely (ledger
            // posting included), and the stored outcome is the answer.
            var stored = await AsPlatformAsync((step, c) => step.ReplayAsync(pos, clientTxnId, tagUid, volumeMl, c), retryOnConflict: false, ct);
            if (stored is null)
            {
                throw;
            }
            return stored;
        }
    }

    /// <summary>
    /// The platform scope, without a user, for a device (and agent) the POS chain verified: the one escalation of the
    /// fueling engine (B4 contract §1, <c>ArchitectureTests</c>). The module contracts run on this transaction.
    /// </summary>
    private Task<T> AsPlatformAsync<T>(Func<Step, CancellationToken, Task<T>> work, bool retryOnConflict, CancellationToken ct) =>
        transactions.RunAsync(DbScopeContext.Escalate(Scope.Platform, null), async tx =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            session.Attach(tx);
            try
            {
                return await work(scope.ServiceProvider.GetRequiredService<Step>(), ct);
            }
            finally
            {
                session.Detach();
            }
        }, retryOnConflict, ct);

    private static void Validate(PosPrincipal pos, FuelingRequest request)
    {
        if (!pos.HasAgent)
        {
            throw new ApiException(401, "AGENT_SESSION_REQUIRED", "An agent must sign in");
        }
        Validate(request.VolumeMl, request.ReceiptLocale);
    }

    internal static void Validate(long volumeMl, string? receiptLocale)
    {
        if (volumeMl is <= 0 or > MaxVolumeMl)
        {
            throw new ApiException(400, "INVALID_VOLUME", "The volume must be between 1 ml and " + MaxVolumeMl + " ml", "volumeMl");
        }
        if (receiptLocale is not "ar" and not "en")
        {
            throw new ApiException(400, ErrorCodes.ValidationFailed, "The receipt language is ar or en", "receiptLocale");
        }
    }

    /// <summary>Microsecond precision, as Java's system clock and PostgreSQL keep it.</summary>
    internal static DateTimeOffset Micros(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));

    /// <summary>
    /// One attempt, on one transaction: the module contracts (ledger, fleet, network, tenancy, pricing) and the fueling
    /// stores, all resolved from the DI scope the transaction is attached to.
    /// </summary>
    internal sealed class Step(Fuelings fuelings, Alerts alerts, ITagGate tags, IStationDirectory stations, ICompanyDirectory companies,
        IPriceBook prices, ILedger ledger, TimeProvider clock, FuelingSettings settings)
    {
        /// <summary>The rows a fueling is judged on, read while their locks are held.</summary>
        private sealed record Locked(Wallet Wallet, TagAtPump Tag, CompanyRef Company, bool Vip);

        public async Task<FuelingOutcome> DecideAsync(PosPrincipal pos, FuelingRequest request, CancellationToken ct)
        {
            if (await ReplayAsync(pos, request.ClientTxnId, request.TagUid, request.VolumeMl, ct) is { } seen)
            {
                return seen;
            }
            var bound = await BoundAsync(request.TagUid, ct);
            var station = await stations.FindStationAsync(pos.StationId, ct) is { Active: true } open
                ? open
                : throw new ApiException(409, "STATION_NOT_ACTIVE", "The station is not open for fuelings");

            var wallet = await ledger.LockWalletAsync(bound.CompanyId, ct);
            if (await ReplayAsync(pos, request.ClientTxnId, request.TagUid, request.VolumeMl, ct) is { } seenUnderLock)
            {
                return seenUnderLock;
            }
            var locked = await LockTagAsync(wallet, bound, ct);
            var tag = locked.Tag;
            var company = locked.Company;

            var now = Micros(clock.GetUtcNow());
            var facts = await FactsAsync(locked, station, request.FuelType, request.VolumeMl, now, tag.CooldownUntil, ct);
            var decision = FuelingRules.Decide(facts);
            // Read before this fueling is written: the VIP ceiling alert fires on the crossing only.
            var vipMonthBefore = await VipMonthBeforeAsync(locked, station, now, ct);

            var id = Guid.CreateVersion7(now);
            var reference = await fuelings.NextReferenceAsync(ct);
            var balanceAfter = wallet.BalancePiastres;
            Guid? ledgerTxn = null;
            if (decision.Approved && decision.Billable && decision.AmountPiastres > 0)
            {
                var posted = await PostAsync(id, reference, [Entry.Debit(Account.CompanyWallet(company.Id), decision.AmountPiastres),
                    Entry.Credit(Account.StationPayable(station.Id), decision.AmountPiastres)], ct);
                ledgerTxn = posted.TransactionId;
                balanceAfter = posted.Wallets[0].BalanceAfter;
            }
            var fueling = new Fueling(id, request.ClientTxnId, reference, decision.Approved ? FuelingStatus.COMPLETED : FuelingStatus.DECLINED,
                decision.DeclineReason, [], FuelingOrigin.ONLINE, company.Id, tag.VehicleId, tag.TagId, station.OperatorId, station.Id,
                pos.DeviceId, pos.AgentId!.Value, FuelOf(tag, request.FuelType), request.VolumeMl, decision.UnitPricePiastres,
                decision.AmountPiastres, decision.Billable, now, request.DeviceAt, now, request.ReceiptLocale, tag.Plate, company.NameAr,
                company.NameEn, station.NameAr, station.NameEn, pos.AgentWorkerId!, ledgerTxn, false);
            await fuelings.InsertAsync(fueling, ct);

            var cooldownUntil = tag.CooldownUntil;
            if (decision.Approved)
            {
                cooldownUntil = now.AddMinutes(company.CooldownMinutes);
                await tags.StartCooldownAsync(tag.TagId, cooldownUntil.Value, ct);
                await RaiseAlertsAsync(fueling, tag, locked.Vip, decision, vipMonthBefore, ct);
            }
            return new FuelingOutcome(fueling, false, StatusOf(company, decision, balanceAfter), cooldownUntil);
        }

        public async Task<Fueling> ResolveAsync(PosPrincipal pos, Guid clientTxnId, bool toldRefused, CancellationToken ct)
        {
            var stored = await fuelings.ByClientTxnIdAsync(clientTxnId, ct) is { } found && found.DeviceId == pos.DeviceId
                ? found
                : throw new ApiException(404, "FUELING_NOT_FOUND", "The server never received this fueling");
            if (stored.Completed && toldRefused && !await alerts.ExistsAsync(AlertType.OUTCOME_MISMATCH, stored.Id, ct))
            {
                await alerts.RaiseAsync(AlertType.OUTCOME_MISMATCH, AlertSeverity.CRITICAL,
                    new AlertSubject(stored.CompanyId, stored.OperatorId, stored.StationId, stored.DeviceId, stored.VehicleId, stored.Id),
                    new Dictionary<string, object?>
                    {
                        ["reference"] = stored.Reference,
                        ["plate"] = stored.Plate,
                        ["amountPiastres"] = stored.AmountPiastres,
                        ["deviceCode"] = pos.DeviceCode,
                    }, true, ct);
            }
            return stored;
        }

        /// <summary>
        /// The stored outcome of a <c>clientTxnId</c>, when this device sent it before with the same fueling. The same id from
        /// another device, or for another volume or tag, is a client error.
        /// </summary>
        public async Task<FuelingOutcome?> ReplayAsync(PosPrincipal pos, Guid clientTxnId, string tagUid, long volumeMl, CancellationToken ct)
        {
            if (await fuelings.ByClientTxnIdAsync(clientTxnId, ct) is not { } stored)
            {
                return null;
            }
            if (stored.DeviceId != pos.DeviceId)
            {
                throw new ApiException(409, "CLIENT_TXN_CONFLICT", "This transaction id was used by another device");
            }
            var sameTag = (await tags.FindAsync(tagUid, ct))?.TagId == stored.TagId;
            if (!sameTag || stored.VolumeMl != volumeMl)
            {
                throw new ApiException(409, "IDEMPOTENCY_MISMATCH", "This transaction id was used for a different fueling");
            }
            return new FuelingOutcome(stored, true, null, null);
        }

        private async Task<BoundTag> BoundAsync(string tagUid, CancellationToken ct) =>
            await tags.FindAsync(tagUid, ct) ?? throw new ApiException(404, "TAG_NOT_RECOGNISED", "This tag is not registered to any vehicle");

        /// <summary>The second and third locks, after the wallet: the vehicle (shared) and the tag.</summary>
        private async Task<Locked> LockTagAsync(Wallet wallet, BoundTag bound, CancellationToken ct)
        {
            var tag = await tags.LockAsync(bound.TagId, ct);
            if (tag.CompanyId != bound.CompanyId)
            {
                // A tag never changes company; if it did, the wallet locked above would be the wrong one.
                throw new InvalidOperationException($"Tag {tag.TagId} changed company during a fueling");
            }
            var company = await companies.FindAsync(tag.CompanyId, ct)
                ?? throw new InvalidOperationException($"Tag {tag.TagId} has no company");
            return new Locked(wallet, tag, company, settings.VipEnabled && tag.Vip);
        }

        /// <summary>What the rules judge, read under the locks, as of <paramref name="at"/>.</summary>
        /// <param name="cooldownUntil">the cooldown that applies at <paramref name="at"/></param>
        private async Task<FuelingRules.Facts> FactsAsync(Locked locked, StationRef station, FuelType? requestedFuel, long volumeMl,
            DateTimeOffset at, DateTimeOffset? cooldownUntil, CancellationToken ct)
        {
            var tag = locked.Tag;
            var fuel = FuelOf(tag, requestedFuel);
            long? unitPrice = station.FuelTypes.Contains(fuel) ? (await prices.PriceAtAsync(station.Id, fuel, at, ct))?.PricePiastresPerLitre : null;
            var limits = locked.Vip ? [] : await LimitsOfAsync(tag, station, at, ct);
            return new FuelingRules.Facts(at, new FuelingRules.Tag(tag.TagActive, cooldownUntil),
                new FuelingRules.Vehicle(tag.VehicleActive, tag.VehicleFuel, locked.Vip, tag.TankCapacityMl),
                new FuelingRules.Company(locked.Company.Active), requestedFuel, unitPrice, limits, locked.Wallet.BalancePiastres, volumeMl);
        }

        private static FuelType FuelOf(TagAtPump tag, FuelType? requestedFuel) => requestedFuel ?? tag.VehicleFuel;

        /// <summary>
        /// Each active limit with what the vehicle used in the period that contains <paramref name="at"/>, in the station's
        /// zone. The whole period counts, also the part after <paramref name="at"/>.
        /// </summary>
        private async Task<List<FuelingRules.Limit>> LimitsOfAsync(TagAtPump tag, StationRef station, DateTimeOffset at, CancellationToken ct)
        {
            var limits = new List<FuelingRules.Limit>();
            foreach (var rule in await tags.ActiveLimitsAsync(tag.VehicleId, ct))
            {
                var from = Periods.Start(at, rule.Period, station.Zone);
                var to = Periods.End(at, rule.Period, station.Zone);
                var used = rule.Kind == FuelingRules.LimitKind.VOLUME
                    ? await fuelings.VolumeInAsync(tag.VehicleId, from, to, ct)
                    : await fuelings.AmountInAsync(tag.VehicleId, from, to, ct);
                limits.Add(new FuelingRules.Limit(rule.Kind, rule.Period, rule.MaxValue, used));
            }
            return limits;
        }

        /// <summary>What a VIP vehicle used in the month of <paramref name="at"/> before this fueling; 0 when no ceiling applies.</summary>
        private async Task<long> VipMonthBeforeAsync(Locked locked, StationRef station, DateTimeOffset at, CancellationToken ct)
        {
            if (!locked.Vip || locked.Tag.VipMonthlyCeilingMl is null)
            {
                return 0;
            }
            return await fuelings.VolumeInAsync(locked.Tag.VehicleId, Periods.Start(at, Periods.Period.MONTH, station.Zone),
                Periods.End(at, Periods.Period.MONTH, station.Zone), ct);
        }

        /// <summary>One posting per fueling: its source is the fueling id, so it can never post twice.</summary>
        private async Task<Posted> PostAsync(Guid fuelingId, string reference, Entry[] entries, CancellationToken ct)
        {
            try
            {
                return await ledger.PostAsync(new Posting(SourceType.FUELING, fuelingId, reference, "Fueling " + reference, Poster.System, entries), ct);
            }
            catch (ApiException ex)
            {
                // Unreachable while the amounts are decided under the wallet lock. If it ever happens, it is a bug, and the
                // ledger's refusal (which names the balance) must not reach the pump.
                throw new InvalidOperationException("The ledger refused a decided fueling: " + ex.Code, ex);
            }
        }

        /// <summary>PRD §10: flags, never blocks.</summary>
        private async Task RaiseAlertsAsync(Fueling fueling, TagAtPump tag, bool vip, FuelingRules.Decision decision, long vipMonthBefore,
            CancellationToken ct)
        {
            var subject = new AlertSubject(fueling.CompanyId, fueling.OperatorId, fueling.StationId, fueling.DeviceId, fueling.VehicleId, fueling.Id);
            if (decision.TankCapacityExceeded)
            {
                await alerts.RaiseAsync(AlertType.TANK_CAPACITY_EXCEEDED, AlertSeverity.CRITICAL, subject, new Dictionary<string, object?>
                {
                    ["reference"] = fueling.Reference,
                    ["plate"] = fueling.Plate,
                    ["volumeMl"] = fueling.VolumeMl,
                    ["tankCapacityMl"] = tag.TankCapacityMl,
                }, true, ct);
            }
            if (vip && tag.VipMonthlyCeilingMl is { } ceiling && vipMonthBefore <= ceiling && vipMonthBefore + fueling.VolumeMl > ceiling)
            {
                await alerts.RaiseAsync(AlertType.VIP_CEILING_PASSED, AlertSeverity.WARNING, subject, new Dictionary<string, object?>
                {
                    ["reference"] = fueling.Reference,
                    ["plate"] = fueling.Plate,
                    ["monthVolumeMl"] = vipMonthBefore + fueling.VolumeMl,
                    ["ceilingMl"] = ceiling,
                }, false, ct);
            }
        }

        private static WalletStatus StatusOf(CompanyRef company, FuelingRules.Decision decision, long balanceAfter)
        {
            if (!company.Active)
            {
                return WalletStatus.SUSPENDED;
            }
            if (decision.DeclineReason == FuelingRules.DeclineReason.INSUFFICIENT_BALANCE)
            {
                return WalletStatus.INSUFFICIENT;
            }
            return balanceAfter < company.LowBalanceThresholdPiastres ? WalletStatus.LOW : WalletStatus.AVAILABLE;
        }
    }
}
