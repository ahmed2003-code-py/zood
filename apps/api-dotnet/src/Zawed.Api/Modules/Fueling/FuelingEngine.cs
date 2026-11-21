using System.Security.Cryptography;
using System.Text;
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

/// <summary>
/// An offline item whose volume or price was not a whole number, as the device sent it (Java <c>FuelingEngine.NotWhole</c>).
/// The item is rejected on its own (<c>INVALID_VOLUME</c>, or <c>VALIDATION_FAILED</c> for the price) and the value is kept
/// for the alert; the rest of the batch is recorded (B5 contract §3).
/// </summary>
public sealed record NotWhole(string Field, string Sent);

/// <summary>A fueling captured while the device was offline (B5 contract §3; Java <c>FuelingEngine.OfflineRequest</c>).</summary>
/// <param name="DeviceAt">when it happened, by the device's clock: the instant the checks are judged at</param>
/// <param name="AgentId">who captured it; trusted only if the server's own session history agrees</param>
/// <param name="DevicePricePiastres">the price per litre the terminal showed, when it sent one</param>
public sealed record OfflineRequest(Guid ClientTxnId, string TagUid, long VolumeMl, FuelType? FuelType, DateTimeOffset DeviceAt,
    Guid AgentId, string ReceiptLocale, long? DevicePricePiastres, NotWhole? NotWhole);

/// <summary>The engine's answer (Java <c>FuelingEngine.Result</c>).</summary>
/// <param name="WalletStatus">null on a replay, which does not read the wallet again</param>
/// <param name="CooldownUntil">null on a replay</param>
public sealed record FuelingOutcome(Fueling Fueling, bool Replayed, WalletStatus? WalletStatus, DateTimeOffset? CooldownUntil);

/// <summary>
/// The fueling engine (plan §3.4, B4 contract §3, ADR 0018; Java <c>FuelingEngine</c>). One transaction
/// per fueling:
/// <list type="number">
/// <item>A <c>clientTxnId</c> seen before returns the stored outcome; nothing is decided twice.</item>
/// <item>The tag's company wallet is locked, then the vehicle (shared) and the tag (always this order, ADR 0004, ADR 0018).
/// Every fueling of a company is therefore serialised: limits and balance cannot be raced.</item>
/// <item>The facts are read under those locks and <see cref="FuelingRules"/> judges them, in PRD §6.1 order.</item>
/// <item>The ledger posting, the fueling row (never updated), the tag's cooldown and any hard alerts are written in the
/// same transaction.</item>
/// </list>
/// <para><b>Online</b> (<see cref="FuelAsync"/>): the balance is checked before anything is written, so the posting that
/// follows never refuses; a failing check declines the fueling and moves no money.</para>
/// <para>A POS has no user. The engine runs in the platform scope (<see cref="DbScopeContext.Escalate"/>), entered only here
/// and only for a device and agent the POS chain has just verified (B4 contract §1); what it writes is bounded by those:
/// the station is the device's, the company is the tag's. Each attempt runs in a transaction of its own, with the module
/// contracts resolved in a DI scope bound to it, and is retried once as a deadlock victim (the attempt rolled back
/// entirely).</para>
/// <para><b>Offline</b> (<see cref="RecordOfflineAsync"/>, B5 contract §4): the fueling already happened at the pump, so
/// it is recorded whatever the checks say. They are judged at the device's time, every one that fails becomes a conflict
/// and an alert, and what the wallet cannot cover becomes a debt on the company's receivable account, so the station is
/// credited in full and the wallet never goes below zero.</para>
/// </summary>
public sealed class FuelingEngine(ITransactions transactions, IServiceScopeFactory scopes)
{
    /// <summary>A tanker truck with two tanks is under 1,500 L; anything larger is a typing error.</summary>
    public const long MaxVolumeMl = 1_500_000;

    /// <summary>B5 contract §3: a capture dated further ahead of server time than this is judged at its arrival.</summary>
    public static readonly TimeSpan MaxClockAhead = TimeSpan.FromMinutes(5);

    /// <summary>B5 contract §3: a capture older than this is still recorded, and flagged.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>How far back one device's offline spending for one company is summed against the bound.</summary>
    public static readonly TimeSpan ExposureWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Conflicts that mean fuel went to something that was blocked, or money that could not be collected the normal way;
    /// the others (a cooldown, a limit, a late capture) are warnings.
    /// </summary>
    private static readonly HashSet<Conflict> CriticalConflicts =
    [
        Conflict.TAG_INACTIVE, Conflict.VEHICLE_INACTIVE, Conflict.COMPANY_FROZEN, Conflict.FUEL_NOT_AVAILABLE, Conflict.INSUFFICIENT_BALANCE,
        Conflict.AGENT_UNVERIFIED, Conflict.DECLINED_ONLINE, Conflict.STATION_NOT_ACTIVE, Conflict.OVER_OFFLINE_ALLOWANCE,
    ];

    /// <summary>Conflicts whose payable waits for Operations before a station payout (B6).</summary>
    private static readonly HashSet<Conflict> HoldPayable = [Conflict.STATION_NOT_ACTIVE, Conflict.OVER_OFFLINE_ALLOWANCE];

    // -------------------------------------------------------------------------------------------------- online

    public async Task<FuelingOutcome> FuelAsync(PosPrincipal pos, FuelingRequest request, CancellationToken ct)
    {
        Validate(pos, request);
        return await OnceAsync(pos, request.ClientTxnId, request.TagUid, request.VolumeMl, (step, c) => step.DecideAsync(pos, request, c), ct);
    }

    // ------------------------------------------------------------------------------------------------- offline

    /// <summary>
    /// Records a fueling that was captured offline (B5 contract §4). It is never declined and never reversed (PRD §7.5):
    /// every check that fails is kept on the fueling as a conflict and raises one alert. The caller gets the stored fueling
    /// back, <c>Replayed</c> when this device sent the same capture before.
    /// </summary>
    /// <param name="device">the device that sends its queue, from its token. Whoever is signed in on it now plays no part:
    /// the capture's agent is <see cref="OfflineRequest.AgentId"/>, checked against the session history</param>
    /// <exception cref="ApiException">when nothing can be recorded: <c>INVALID_VOLUME</c>, <c>VALIDATION_FAILED</c>,
    /// <c>TAG_NOT_RECOGNISED</c>, <c>AGENT_NOT_OF_STATION</c>, <c>CLIENT_TXN_CONFLICT</c>, <c>IDEMPOTENCY_MISMATCH</c></exception>
    public async Task<FuelingOutcome> RecordOfflineAsync(PosPrincipal device, OfflineRequest request, CancellationToken ct)
    {
        Validate(request);
        var result = await OnceAsync(device, request.ClientTxnId, request.TagUid, request.VolumeMl,
            (step, c) => step.RecordAsync(device, request, false, c), ct);
        if (result.Replayed && !result.Fueling.Completed)
        {
            // The terminal sent this fueling online, the server declined it, the answer never arrived, and the terminal then
            // let the fuel go on its offline rules under the same id. The id's stored outcome is the decline and stays so (a
            // repeated id changes nothing), but the fuel left the pump: it is recorded as an offline fueling of its own, under
            // an id derived from the first so that sending it again finds it, flagged DECLINED_ONLINE (B5 review 1).
            var after = request with { ClientTxnId = AfterDecline(request.ClientTxnId) };
            return await OnceAsync(device, after.ClientTxnId, after.TagUid, after.VolumeMl, (step, c) => step.RecordAsync(device, after, true, c), ct);
        }
        return result;
    }

    /// <summary>
    /// The id an offline capture is recorded under after its online attempt was declined: Java's
    /// <c>UUID.nameUUIDFromBytes("offline-after-decline:" + clientTxnId)</c>, a version 3 (MD5) name UUID, so both APIs derive
    /// the same id. MD5 names an id here; it protects nothing.
    /// </summary>
    public static Guid AfterDecline(Guid clientTxnId)
    {
#pragma warning disable CA5351 // the version 3 UUID of RFC 4122 is defined on MD5; this is an id, not a security measure
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("offline-after-decline:" + clientTxnId.ToString("D")));
#pragma warning restore CA5351
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash, bigEndian: true);
    }

    /// <summary>
    /// What became of an online fueling whose answer the terminal never received (B5 review 6). The stored fueling of this
    /// device's <c>clientTxnId</c>, or 404 when the server never got it. When the server completed it and the agent was told
    /// it was refused, the company was charged for a fueling nobody thinks happened: Operations and the company are alerted,
    /// once.
    /// <para>Two resolves at once (wave 5B, review of 3A) can both find no mismatch alert and race to insert it: the unique
    /// index <see cref="Alerts.OutcomeMismatchOnce"/> lets one through, and the other's attempt rolls back entirely. It is
    /// answered by running again, which now finds the alert and returns the stored fueling. Java answers that second request
    /// with a 500; the stored fueling is the answer to the question the terminal asked, so this is a deliberate improvement.</para>
    /// </summary>
    public async Task<Fueling> ResolveAsync(PosPrincipal pos, Guid clientTxnId, bool toldRefused, CancellationToken ct)
    {
        try
        {
            return await AsPlatformAsync((step, c) => step.ResolveAsync(pos, clientTxnId, toldRefused, c), retryOnConflict: false, ct);
        }
        catch (Exception ex) when (DbErrors.IsUniqueViolation(ex, Alerts.OutcomeMismatchOnce))
        {
            return await AsPlatformAsync((step, c) => step.ResolveAsync(pos, clientTxnId, toldRefused, c), retryOnConflict: false, ct);
        }
    }

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

    private static void Validate(OfflineRequest request)
    {
        if (request.NotWhole is { } notWhole)
        {
            throw new ApiException(400, notWhole.Field == "volumeMl" ? "INVALID_VOLUME" : ErrorCodes.ValidationFailed, "Must be a whole number",
                notWhole.Field, new Dictionary<string, object> { ["sentValue"] = notWhole.Sent });
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
        IPriceBook prices, ILedger ledger, IAgentShifts shifts, IDbSession session, TimeProvider clock, FuelingSettings settings)
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

        /// <summary>Java <c>FuelingEngine.record</c>: one offline capture, in this attempt's transaction.</summary>
        public async Task<FuelingOutcome> RecordAsync(PosPrincipal device, OfflineRequest request, bool declinedOnline, CancellationToken ct)
        {
            if (await ReplayAsync(device, request.ClientTxnId, request.TagUid, request.VolumeMl, ct) is { } seen)
            {
                return seen;
            }
            var bound = await BoundAsync(request.TagUid, ct);
            var now = Micros(clock.GetUtcNow());
            var conflicts = new List<Conflict>();
            // A capture dated in the future is judged at the time it arrived (B5 review 1): it cannot have happened later than
            // that, and a future date would push prices, periods and the cooldown ahead.
            var at = request.DeviceAt;
            if (at > now + MaxClockAhead)
            {
                at = now;
                conflicts.Add(Conflict.DEVICE_CLOCK_SKEW);
            }
            // The agent must be of this station; whose session cannot be confirmed for the time is recorded and flagged,
            // because the fuel left the pump either way (B5 review 1).
            var onDevice = await shifts.OnDeviceAtAsync(session.Current, device.DeviceId, device.StationId, request.AgentId, at, ct);
            var agent = onDevice ?? await shifts.OfStationAsync(session.Current, device.StationId, request.AgentId, ct)
                ?? throw new ApiException(403, "AGENT_NOT_OF_STATION", "This agent does not work at this device's station");
            if (onDevice is null)
            {
                conflicts.Add(Conflict.AGENT_UNVERIFIED);
            }
            if (declinedOnline)
            {
                conflicts.Add(Conflict.DECLINED_ONLINE);
            }
            // Not required to be open, unlike online: the fuel was dispensed and is recorded (PRD §7.5), but a station that was
            // not open is flagged and its payable held (B5 review 7).
            var station = await stations.FindStationAsync(device.StationId, ct)
                ?? throw new InvalidOperationException($"Device {device.DeviceId} has no station");
            if (!station.Active)
            {
                conflicts.Add(Conflict.STATION_NOT_ACTIVE);
            }

            var wallet = await ledger.LockWalletAsync(bound.CompanyId, ct);
            if (await ReplayAsync(device, request.ClientTxnId, request.TagUid, request.VolumeMl, ct) is { } seenUnderLock)
            {
                return seenUnderLock;
            }
            var locked = await LockTagAsync(wallet, bound, ct);
            var tag = locked.Tag;
            var company = locked.Company;

            // The same checks as online, judged at the device's time: the price in force then, the limits of the period that
            // contains it, and the cooldown as the fuelings around it give it.
            var cooldown = TimeSpan.FromMinutes(company.CooldownMinutes);
            var facts = await FactsAsync(locked, station, request.FuelType, request.VolumeMl, at, await CooldownAroundAsync(tag, cooldown, at, ct), ct);
            var decision = FuelingRules.Decide(facts);
            foreach (var reason in FuelingRules.AllFailures(facts))
            {
                conflicts.Add(ConflictOf(reason));
            }
            if (at < now - StaleAfter)
            {
                conflicts.Add(Conflict.STALE_CAPTURE);
            }
            var vipMonthBefore = await VipMonthBeforeAsync(locked, station, at, ct);

            // The price. With none in force at the capture time (the fuel left the station's list after the snapshot, or the
            // device's clock is before the first price), the one in force now is charged, so a dispensed fueling is never worth
            // nothing (B5 review 9).
            var unitPrice = decision.UnitPricePiastres;
            var amount = decision.AmountPiastres;
            if (unitPrice == 0)
            {
                var fuel = FuelOf(tag, request.FuelType);
                var fallback = (await prices.PriceAtAsync(station.Id, fuel, at, ct) ?? await prices.PriceAtAsync(station.Id, fuel, now, ct))
                    ?.PricePiastresPerLitre;
                if (fallback is { } price)
                {
                    unitPrice = price;
                    amount = MoneyMath.AmountForVolume(request.VolumeMl, unitPrice);
                }
            }
            if (request.DevicePricePiastres is { } shown && shown != unitPrice)
            {
                conflicts.Add(Conflict.PRICE_MISMATCH);
            }
            // The bound on what one device may spend offline for one company (B5 review 2): the terminal keeps to its
            // allowance, but only an honest terminal does. Past the bound the fueling is still recorded, flagged, its payable
            // held for Operations, and a critical alert raised.
            var overExposure = decision.Billable && amount > 0
                && await fuelings.OfflineAmountSinceAsync(device.DeviceId, company.Id, at - ExposureWindow, ct) + amount > settings.ExposureCapPiastres;
            if (overExposure)
            {
                conflicts.Add(Conflict.OVER_OFFLINE_ALLOWANCE);
            }
            var held = conflicts.Any(HoldPayable.Contains);

            // The money (B5 contract §4). The station dispensed the fuel and is credited in full. The wallet pays what it holds,
            // read under its lock, so it stops at exactly zero; the rest is the company's debt on its receivable account. One
            // balanced posting for the fueling.
            var id = Guid.CreateVersion7(now);
            var reference = await fuelings.NextReferenceAsync(ct);
            long owed = 0;
            Guid? ledgerTxn = null;
            if (decision.Billable && amount > 0)
            {
                var fromWallet = Math.Min(wallet.BalancePiastres, amount);
                owed = amount - fromWallet;
                var entries = new List<Entry>();
                if (fromWallet > 0)
                {
                    entries.Add(Entry.Debit(Account.CompanyWallet(company.Id), fromWallet));
                }
                if (owed > 0)
                {
                    entries.Add(Entry.Debit(Account.CompanyReceivable(company.Id), owed));
                }
                entries.Add(Entry.Credit(Account.StationPayable(station.Id), amount));
                ledgerTxn = (await PostAsync(id, reference, [.. entries], ct)).TransactionId;
            }
            var fueling = new Fueling(id, request.ClientTxnId, reference, FuelingStatus.COMPLETED, null, conflicts.AsReadOnly(),
                FuelingOrigin.OFFLINE, company.Id, tag.VehicleId, tag.TagId, station.OperatorId, station.Id, device.DeviceId, agent.AgentId,
                FuelOf(tag, request.FuelType), request.VolumeMl, unitPrice, amount, decision.Billable, at, request.DeviceAt, now,
                request.ReceiptLocale, tag.Plate, company.NameAr, company.NameEn, station.NameAr, station.NameEn, agent.WorkerId, ledgerTxn, held);
            await fuelings.InsertAsync(fueling, ct);

            // The mirror of the tag's cooldown only ever moves forward: a capture older than the tag's last fueling must not
            // shorten the cooldown that fueling started.
            var cooldownUntil = at + cooldown;
            if (tag.CooldownUntil is null || cooldownUntil > tag.CooldownUntil)
            {
                await tags.StartCooldownAsync(tag.TagId, cooldownUntil, ct);
            }
            else
            {
                cooldownUntil = tag.CooldownUntil.Value;
            }
            await RaiseAlertsAsync(fueling, tag, locked.Vip, decision, vipMonthBefore, ct);
            if (conflicts.Count > 0)
            {
                await RaiseConflictAsync(fueling, owed, ct);
            }
            if (overExposure)
            {
                await alerts.RaiseAsync(AlertType.OFFLINE_EXPOSURE, AlertSeverity.CRITICAL, SubjectOf(fueling), new Dictionary<string, object?>
                {
                    ["reference"] = fueling.Reference,
                    ["deviceCode"] = device.DeviceCode,
                    ["capPiastres"] = settings.ExposureCapPiastres,
                    ["amountPiastres"] = amount,
                }, false, ct);
            }
            // Company-wide monitoring (V14, ADR 0025): raised once, on the fueling that crosses the threshold across every
            // device in the last 24 hours. It never changes the decision above.
            //
            // No lock of its own: the sum and the decision are read under the company's wallet lock, taken first above, which
            // every fueling of the company takes (ADR 0018). Under READ COMMITTED each statement here sees every fueling
            // committed before this one got the lock, so concurrent syncs from different devices decide one after the other
            // and exactly one sees the crossing (CompanyOfflineMonitoringTests).
            //
            // The window is by evaluated_at, the capture's own time: a late capture from outside the last 24 hours is not in
            // the sum, so it can never be the one that crosses.
            if (amount > 0)
            {
                var windowStart = Micros(clock.GetUtcNow()) - ExposureWindow;
                var since24h = await fuelings.CompanyOfflineAmountSinceAsync(fueling.CompanyId, windowStart, ct);
                var inWindow = fueling.EvaluatedAt > windowStart;
                var threshold = settings.CompanyOfflineAlertPiastres;
                if (inWindow && since24h >= threshold && since24h - amount < threshold)
                {
                    await alerts.RaiseAsync(AlertType.COMPANY_OFFLINE_EXPOSURE, AlertSeverity.WARNING, SubjectOf(fueling),
                        new Dictionary<string, object?>
                        {
                            ["reference"] = fueling.Reference,
                            ["offline24hPiastres"] = since24h,
                            ["thresholdPiastres"] = threshold,
                        }, false, ct);
                }
            }
            return new FuelingOutcome(fueling, false, null, cooldownUntil);
        }

        /// <summary>The conflict of a decline reason: the same name (Java <c>Conflict.of</c>).</summary>
        internal static Conflict ConflictOf(FuelingRules.DeclineReason reason) => Enum.Parse<Conflict>(reason.ToString());

        private static AlertSubject SubjectOf(Fueling fueling) =>
            new(fueling.CompanyId, fueling.OperatorId, fueling.StationId, fueling.DeviceId, fueling.VehicleId, fueling.Id);

        /// <summary>
        /// The cooldown an offline capture at <paramref name="at"/> collides with, or null. The tag's own <c>cooldown_until</c>
        /// is the mirror of its latest fueling, which may be one that happened after this capture and far enough from it;
        /// judging the capture by it would flag a conflict that never was. So the fuelings themselves are asked: two fuelings
        /// of one vehicle closer than the cooldown are a conflict, whichever of them reached the server last. (The cooldown
        /// follows the vehicle: a replacement tag inherits it.)
        /// </summary>
        private async Task<DateTimeOffset?> CooldownAroundAsync(TagAtPump tag, TimeSpan cooldown, DateTimeOffset at, CancellationToken ct) =>
            await fuelings.LatestCompletedBetweenAsync(tag.VehicleId, at - cooldown, at + cooldown, ct) is { } other ? other + cooldown : null;

        /// <summary>One alert per offline fueling with conflicts, for Operations and the company (B5 contract §4).</summary>
        private async Task RaiseConflictAsync(Fueling fueling, long owedPiastres, CancellationToken ct)
        {
            var critical = fueling.Conflicts.Any(CriticalConflicts.Contains);
            var details = new Dictionary<string, object?>
            {
                ["reference"] = fueling.Reference,
                ["plate"] = fueling.Plate,
                ["conflicts"] = fueling.Conflicts.Select(c => c.ToString()).ToList(),
                ["deviceAt"] = JavaInstant.Text(fueling.DeviceAt),
                ["volumeMl"] = fueling.VolumeMl,
                ["amountPiastres"] = fueling.AmountPiastres,
            };
            if (owedPiastres > 0)
            {
                // The part of the amount the wallet could not cover: the company's own debt.
                details["owedPiastres"] = owedPiastres;
            }
            await alerts.RaiseAsync(AlertType.SYNC_CONFLICT, critical ? AlertSeverity.CRITICAL : AlertSeverity.WARNING, SubjectOf(fueling),
                details, true, ct);
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
