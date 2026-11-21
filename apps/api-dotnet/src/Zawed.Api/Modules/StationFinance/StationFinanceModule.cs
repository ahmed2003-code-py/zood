using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.StationFinance;

/// <summary>
/// Station finance (Java package <c>stationfinance</c>; plan §2, phase B6; roadmap delta A1, A3, A4; ADR 0020): where a
/// station is paid (bank accounts under change control), what it is paid (payouts with dual control), what waits (held
/// payables and their release), what it can read (the statement of its payable account) and how a completed fueling is
/// corrected (a two-person reversal).
/// <para>This module never writes money. It asks the ledger (<c>ILedger</c>) to post, inside its own transaction, and
/// keeps the business records around the posting. Every decision here is taken while the station's payable is locked
/// (<c>ILedger.LockPayableAsync</c>), after any wallet lock. It publishes <c>PayoutPosted</c> and
/// <c>BankAccountChangeDecided</c> (<c>Contracts</c>) for the notifications.</para>
/// </summary>
public sealed class StationFinanceModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Read at boot, as Java's @Value did: a negative value stops the start.
        services.AddSingleton(StationFinanceSettings.From(configuration));
        services.AddScoped<StationAccess>();
        services.AddScoped<PayableBook>();
        services.AddScoped<BankAccounts>();
        services.AddScoped<Payouts>();
        services.AddScoped<PayableHolds>();
        services.AddScoped<Reversals>();
        services.AddScoped<StationStatements>();
        services.AddScoped<StationFinanceViews>();
        // Bank account numbers, bound exactly as BankAccounts binds them, for the data-key rotation (ADR 0027). The
        // database audits every change of the column (BANK_ACCOUNT_REKEYED, V20).
        services.AddSingleton(new EncryptedColumn("station_bank_accounts", "account_encrypted", BankAccounts.Binding));
    }
}
