using Npgsql;
using Zawed.Api.Modules.Ledger;
using Zawed.Api.Modules.Ledger.Contracts;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;
using Zawed.Tests.Support;
using static Zawed.Tests.Support.IdentityTestSupport;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// The posting engine and the guarantees the database gives it (plan §3.5; Java <c>LedgerTests</c>): parallel credits
/// and debits through real transactions, balance and immutability enforced by PostgreSQL for every role, one posting
/// per source, a wallet that never goes negative, the low-balance crossing.
/// </summary>
public sealed class LedgerTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private readonly IdentityTestSupport id = new(api.Factory);

    // ------------------------------------------------------------------------------------------------- concurrency

    [Fact]
    public async Task ParallelCreditsAndDebitsLeaveTheCachedBalanceEqualToTheLedgerAndNeverNegative()
    {
        var company = await id.SeedCompanyAsync();
        const int credits = 10;
        const int debits = 10;
        const long credit = 1_000;
        const long debit = 700;

        var outcomes = await Concurrently.RunAsync(credits + debits, async i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), credit),
                        Entry.Credit(Account.CompanyWallet(company), credit));
                    return "CREDIT";
                }
                await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyWallet(company), debit),
                    Entry.Credit(Account.PlatformBank(), debit));
                return "DEBIT";
            }
            catch (ApiException ex)
            {
                Assert.Equal("INSUFFICIENT_BALANCE", ex.Code);
                return "REFUSED";
            }
        });

        long creditsPosted = outcomes.Count(o => o == "CREDIT");
        long debitsPosted = outcomes.Count(o => o == "DEBIT");
        Assert.Equal(credits, creditsPosted);
        var expected = creditsPosted * credit - debitsPosted * debit;
        Assert.True(expected >= 0);
        Assert.Equal(expected, await api.CachedBalanceAsync(company));
        Assert.Equal(expected, await api.LedgerBalanceAsync(company));

        // The running balance stored on each entry is a consistent chain in insert order.
        var chain = await api.Owner.QueryAsync<(string Direction, long Amount, long After)>(
            "select direction, amount_piastres, balance_after_piastres from ledger_entries where company_id = @company order by seq", new { company });
        long running = 0;
        foreach (var entry in chain)
        {
            running += entry.Direction == "CREDIT" ? entry.Amount : -entry.Amount;
            Assert.True(running >= 0);
            Assert.Equal(running, entry.After);
        }
        Assert.Equal(expected, running);
    }

    [Fact]
    public async Task ADuplicateSourcePostsOnceEvenUnderARace()
    {
        var company = await id.SeedCompanyAsync();
        var source = Guid.NewGuid();
        var outcomes = await Concurrently.RunAsync(2, async _ =>
        {
            try
            {
                await api.PostAsync(SourceType.ADJUSTMENT, source, Entry.Debit(Account.PlatformBank(), 5_000),
                    Entry.Credit(Account.CompanyWallet(company), 5_000));
                return "POSTED";
            }
            catch (ApiException ex)
            {
                return ex.Code;
            }
        });
        Assert.Equal(new[] { "ALREADY_POSTED", "POSTED" }, outcomes.Order());
        Assert.Equal(5_000, await api.CachedBalanceAsync(company));
        Assert.Equal(1, await api.CountAsync("select count(*) from ledger_transactions where source_id = @source", new { source }));

        // And again, sequentially: the answer is the same and nothing changes.
        var again = await Assert.ThrowsAsync<ApiException>(() => api.PostAsync(SourceType.ADJUSTMENT, source,
            Entry.Debit(Account.PlatformBank(), 5_000), Entry.Credit(Account.CompanyWallet(company), 5_000)));
        Assert.Equal("ALREADY_POSTED", again.Code);
        Assert.Equal(5_000, await api.CachedBalanceAsync(company));
    }

    [Fact]
    public async Task ManyPostingsRacedOnOneWalletKeepEveryBalanceAfterConsistentAndRefuseNothingThatFits()
    {
        // Beyond the Java class: 30 credits of different sizes at once on one wallet, then 30 debits that together take
        // it exactly to zero. Every entry's balance_after continues the chain; nothing is refused, nothing is lost.
        var company = await id.SeedCompanyAsync();
        var amounts = Enumerable.Range(1, 30).Select(i => (long)i * 101).ToList();
        await Concurrently.RunAsync(amounts.Count, i => api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(),
            Entry.Debit(Account.PlatformBank(), amounts[i]), Entry.Credit(Account.CompanyWallet(company), amounts[i])));
        Assert.Equal(amounts.Sum(), await api.CachedBalanceAsync(company));
        await Concurrently.RunAsync(amounts.Count, i => api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(),
            Entry.Debit(Account.CompanyWallet(company), amounts[i]), Entry.Credit(Account.PlatformBank(), amounts[i])));
        Assert.Equal(0, await api.CachedBalanceAsync(company));
        Assert.Equal(0, await api.LedgerBalanceAsync(company));
        var chain = await api.Owner.QueryAsync<(string Direction, long Amount, long After)>(
            "select direction, amount_piastres, balance_after_piastres from ledger_entries where company_id = @company order by seq", new { company });
        Assert.Equal(60, chain.Count);
        long running = 0;
        foreach (var entry in chain)
        {
            running += entry.Direction == "CREDIT" ? entry.Amount : -entry.Amount;
            Assert.Equal(running, entry.After);
        }
    }

    // ----------------------------------------------------------------------------------- the database's own word

    [Fact]
    public async Task TheDatabaseRejectsAnUnbalancedTransactionForEveryRole()
    {
        var company = await id.SeedCompanyAsync();
        var bank = await BankAccountAsync();
        var wallet = await WalletAccountAsync(company);
        foreach (var asApp in new[] { true, false })
        {
            var (connection, tx) = await RawSql.OpenAsync(api.Owner, asApp, "PLATFORM");
            await using (connection)
            {
                var txn = await InsertTransactionAsync(connection, tx, company);
                await InsertEntryAsync(connection, tx, txn, bank, null, "DEBIT", 100, null);
                await InsertEntryAsync(connection, tx, txn, wallet, company, "CREDIT", 50, 50);
                Assert.Contains("does not balance", (await RawSql.RefusedAsync(() => tx.CommitAsync())).Message);
            }
            (connection, tx) = await RawSql.OpenAsync(api.Owner, asApp, "PLATFORM");
            await using (connection)
            {
                var txn = await InsertTransactionAsync(connection, tx, company);
                await InsertEntryAsync(connection, tx, txn, bank, null, "DEBIT", 100, null);
                Assert.Contains("at least two", (await RawSql.RefusedAsync(() => tx.CommitAsync())).Message);
            }
            (connection, tx) = await RawSql.OpenAsync(api.Owner, asApp, "PLATFORM");
            await using (connection)
            {
                await InsertTransactionAsync(connection, tx, company);
                Assert.Contains("at least two", (await RawSql.RefusedAsync(() => tx.CommitAsync())).Message);
            }
        }
        Assert.Equal(0, await api.CountAsync("select count(*) from ledger_transactions where company_id = @company", new { company }));
    }

    [Fact]
    public async Task LedgerEntriesAndTransactionsCanNeverBeUpdatedOrDeleted()
    {
        var company = await id.SeedCompanyAsync();
        var posted = await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 300),
            Entry.Credit(Account.CompanyWallet(company), 300));
        var txn = posted.TransactionId;

        foreach (var sql in new[]
        {
            $"update ledger_entries set amount_piastres = 1 where transaction_id = '{txn}'",
            $"delete from ledger_entries where transaction_id = '{txn}'",
            $"delete from ledger_transactions where id = '{txn}'",
        })
        {
            var (connection, tx) = await RawSql.OpenAsync(api.Owner, true, "PLATFORM");
            await using (connection)
            {
                Assert.Contains("permission denied", (await RawSql.RefusedAsync(() => RawSql.ExecuteAsync(connection, tx, sql))).Message);
            }
        }
        // The owner has the grants and is still refused, by the trigger.
        foreach (var sql in new[]
        {
            "update ledger_entries set amount_piastres = 1 where transaction_id = @txn",
            "delete from ledger_entries where transaction_id = @txn",
            "update ledger_transactions set reference = 'x' where id = @txn",
            "delete from ledger_transactions where id = @txn",
            "truncate ledger_entries",
        })
        {
            Assert.Contains("immutable", (await RawSql.RefusedAsync(() => api.Owner.ExecuteAsync(sql, new { txn }))).Message);
        }
        Assert.Equal(300, await api.LedgerBalanceAsync(company));
    }

    [Fact]
    public async Task AWalletNeverGoesBelowZero()
    {
        var company = await id.SeedCompanyAsync();
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 1_000),
            Entry.Credit(Account.CompanyWallet(company), 1_000));
        var refused = await Assert.ThrowsAsync<ApiException>(() => api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(),
            Entry.Debit(Account.CompanyWallet(company), 1_001), Entry.Credit(Account.PlatformBank(), 1_001)));
        Assert.Equal("INSUFFICIENT_BALANCE", refused.Code);
        Assert.Equal(409, refused.Status);
        Assert.Equal(1_000L, refused.Params!["balancePiastres"]);
        Assert.Equal(1_001L, refused.Params!["requiredPiastres"]);
        Assert.Equal(1_000, await api.CachedBalanceAsync(company));
        Assert.Equal(1_000, await api.LedgerBalanceAsync(company));
        // Not even the owner can write a negative cached balance.
        Assert.Contains("check", (await RawSql.RefusedAsync(() => api.Owner.ExecuteAsync(
            "update company_wallets set balance_piastres = -1 where company_id = @company", new { company }))).Message);
    }

    [Fact]
    public async Task ARefusedPostingChangesNothing()
    {
        // Beyond the Java class: a posting refused half-way (the second wallet cannot cover its debit) leaves the first
        // wallet, the transactions, the entries and the audit as they were.
        var rich = await id.SeedCompanyAsync();
        var poor = await id.SeedCompanyAsync();
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 900), Entry.Credit(Account.CompanyWallet(rich), 900));
        var entriesBefore = await api.CountAsync("select count(*) from ledger_entries where company_id in (@rich, @poor)", new { rich, poor });
        var source = Guid.NewGuid();
        var refused = await Assert.ThrowsAsync<ApiException>(() => api.PostAsync(SourceType.ADJUSTMENT, source,
            Entry.Debit(Account.CompanyWallet(rich), 500), Entry.Debit(Account.CompanyWallet(poor), 1), Entry.Credit(Account.PlatformBank(), 501)));
        Assert.Equal("INSUFFICIENT_BALANCE", refused.Code);
        Assert.Equal(900, await api.CachedBalanceAsync(rich));
        Assert.Equal(0, await api.CachedBalanceAsync(poor));
        Assert.Equal(entriesBefore, await api.CountAsync("select count(*) from ledger_entries where company_id in (@rich, @poor)", new { rich, poor }));
        Assert.Equal(0, await api.CountAsync("select count(*) from ledger_transactions where source_id = @source", new { source }));
        // The source is still free: the same posting, once it fits, goes through.
        await api.PostAsync(SourceType.ADJUSTMENT, source, Entry.Debit(Account.CompanyWallet(rich), 500), Entry.Credit(Account.PlatformBank(), 500));
        Assert.Equal(400, await api.CachedBalanceAsync(rich));
    }

    [Fact]
    public void AnUnbalancedPostingIsRefusedBeforeItReachesTheDatabase()
    {
        var company = Guid.CreateVersion7();
        Assert.Contains("balance", Assert.Throws<ArgumentException>(() => new Posting(SourceType.ADJUSTMENT, Guid.NewGuid(), null, null, Poster.System,
            [Entry.Debit(Account.PlatformBank(), 100), Entry.Credit(Account.CompanyWallet(company), 99)])).Message);
        Assert.Throws<ArgumentException>(() => new Posting(SourceType.ADJUSTMENT, Guid.NewGuid(), null, null, Poster.System,
            [Entry.Debit(Account.PlatformBank(), 100)]));
        Assert.Throws<ArgumentException>(() => Entry.Debit(Account.PlatformBank(), 0));
        Assert.Throws<ArgumentException>(() => new Account(AccountKind.PLATFORM_BANK, company));
        Assert.Throws<ArgumentException>(() => new Account(AccountKind.COMPANY_WALLET, null));
    }

    // ------------------------------------------------------------------------------------------------- low balance

    [Fact]
    public async Task CrossingTheThresholdDownwardsIsRecordedOnce()
    {
        var company = await id.SeedCompanyAsync(50_000);
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 100_000),
            Entry.Credit(Account.CompanyWallet(company), 100_000));
        Assert.Equal(0, await LowBalanceEventsAsync(company));

        var crossing = await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyWallet(company), 60_000),
            Entry.Credit(Account.PlatformBank(), 60_000));
        Assert.True(crossing.Wallets[0].CrossedBelowThreshold);
        Assert.Equal(1, await LowBalanceEventsAsync(company));

        // Already below: no second record.
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyWallet(company), 10_000),
            Entry.Credit(Account.PlatformBank(), 10_000));
        Assert.Equal(1, await LowBalanceEventsAsync(company));

        // Back above, then below again: recorded again.
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 30_000),
            Entry.Credit(Account.CompanyWallet(company), 30_000));
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyWallet(company), 20_000),
            Entry.Credit(Account.PlatformBank(), 20_000));
        Assert.Equal(2, await LowBalanceEventsAsync(company));

        // The domain event (for the alerts and the notifications) goes with each record, carrying the posting's facts.
        var published = api.Events.Of<WalletLowBalance>().Where(e => e.CompanyId == company).ToList();
        Assert.Equal(2, published.Count);
        Assert.Equal((40_000L, 50_000L, crossing.TransactionId), (published[0].BalancePiastres, published[0].ThresholdPiastres, published[0].TransactionId));
    }

    // -------------------------------------------------------------------------------------------------- receivable

    [Fact]
    public async Task AReceivableTakesWhatTheWalletCannotCoverAndLeavesTheWalletRulesAlone()
    {
        var company = await id.SeedCompanyAsync();
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 1_000),
            Entry.Credit(Account.CompanyWallet(company), 1_000));

        // What an offline fueling the wallet cannot cover posts (B5): the wallet gives what it holds, the receivable the
        // rest, and the other side is credited in full.
        var split = await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyWallet(company), 1_000),
            Entry.Debit(Account.CompanyReceivable(company), 400), Entry.Credit(Account.PlatformBank(), 1_400));
        Assert.Single(split.Wallets);
        Assert.Equal(0, split.Wallets[0].BalanceAfter);
        Assert.Equal(0, await api.CachedBalanceAsync(company));
        Assert.Equal(0, await api.AccountBalanceAsync(Account.CompanyWallet(company)));
        Assert.Equal(-400, await api.AccountBalanceAsync(Account.CompanyReceivable(company))); // a debit is money owed

        // Wholly on the receivable: no wallet is locked, moved or reported.
        var owed = await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyReceivable(company), 250),
            Entry.Credit(Account.PlatformBank(), 250));
        Assert.Empty(owed.Wallets);
        Assert.Equal(0, await api.CachedBalanceAsync(company));
        Assert.Equal(-650, await api.AccountBalanceAsync(Account.CompanyReceivable(company)));

        // The account is the company's, opened once; its entries carry the company and no running balance (that column
        // is the wallet's); the transaction is the company's too.
        Assert.Equal(1, await api.CountAsync(
            "select count(*) from ledger_accounts where company_id = @company and kind = 'COMPANY_RECEIVABLE'", new { company }));
        Assert.Equal(2, await api.CountAsync("""
            select count(*) from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where a.code = @code and e.company_id = @company and e.balance_after_piastres is null
            """, new { code = Account.CompanyReceivable(company).Code, company }));
        Assert.Equal(company, await api.Owner.QueryOneAsync<Guid>("select company_id from ledger_transactions where id = @id", new { id = owed.TransactionId }));
        Assert.Equal(2, await api.CountAsync(
            "select count(*) from audit_log where action = 'COMPANY_RECEIVABLE_RAISED' and entity_id = @company", new { company }));

        // The wallet's floor is untouched by all this: it still refuses to go below zero.
        var refused = await Assert.ThrowsAsync<ApiException>(() => api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(),
            Entry.Debit(Account.CompanyWallet(company), 1), Entry.Credit(Account.PlatformBank(), 1)));
        Assert.Equal("INSUFFICIENT_BALANCE", refused.Code);
    }

    [Fact]
    public async Task AReceivableRacedOnItsFirstUseIsOpenedOnce()
    {
        var company = await id.SeedCompanyAsync();
        await Concurrently.RunAsync(8, _ => api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(),
            Entry.Debit(Account.CompanyReceivable(company), 100), Entry.Credit(Account.PlatformBank(), 100)));
        Assert.Equal(1, await api.CountAsync("select count(*) from ledger_accounts where code = @code",
            new { code = Account.CompanyReceivable(company).Code }));
        Assert.Equal(-800, await api.AccountBalanceAsync(Account.CompanyReceivable(company)));
    }

    [Fact]
    public async Task ACompanyReadsItsOwnReceivableAndNoOtherCompanys()
    {
        var mine = await id.SeedCompanyAsync();
        var theirs = await id.SeedCompanyAsync();
        foreach (var company in new[] { mine, theirs })
        {
            await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.CompanyReceivable(company), 700),
                Entry.Credit(Account.PlatformBank(), 700));
        }
        var (connection, tx) = await RawSql.OpenAsync(api.Owner, true, "COMPANY", mine);
        await using (connection)
        {
            var rows = new List<(Guid, long)>();
            await using (var command = new NpgsqlCommand("""
                select a.company_id, e.amount_piastres from ledger_entries e join ledger_accounts a on a.id = e.account_id
                where a.kind = 'COMPANY_RECEIVABLE'
                """, connection, tx))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
                }
            }
            Assert.Equal(new[] { (mine, 700L) }, rows); // another company's debt is not visible
            // And it cannot write its own debt away.
            Assert.Contains("row-level security", (await RawSql.RefusedAsync(() => RawSql.ExecuteAsync(connection, tx,
                $"insert into ledger_accounts (id, code, kind, company_id, created_at) values ('{Guid.NewGuid()}', 'company:{Guid.NewGuid()}:receivable', "
                + $"'COMPANY_RECEIVABLE', '{mine}', now())"))).Message);
        }
    }

    // ------------------------------------------------------------------------------------------ wallet provisioning

    [Fact]
    public async Task ANewCompanyGetsAnEmptyWalletInTheSameTransaction()
    {
        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        var created = await id.AuthedAsync("POST", "/api/v1/ops/companies", ops.AccessToken,
            "{\"company\":{\"legalName\":\"Wallet Co\",\"nameAr\":\"شركة\",\"nameEn\":\"Wallet Co\"},\"lowBalanceThresholdPiastres\":250000}");
        Assert.True((int)created.StatusCode == 201, await created.Content.ReadAsStringAsync());
        var company = Guid.Parse((await JsonOf(created))["id"]!.GetValue<string>());

        var wallet = await id.AuthedAsync("GET", $"/api/v1/ops/companies/{company}/wallet", ops.AccessToken);
        Assert.True((int)wallet.StatusCode == 200, await wallet.Content.ReadAsStringAsync());
        var body = await JsonOf(wallet);
        Assert.Equal(0, body["balancePiastres"]!.GetValue<long>());
        Assert.Equal(250_000, body["lowBalanceThresholdPiastres"]!.GetValue<long>());
        Assert.True(body["lowBalance"]!.GetValue<bool>());
        Assert.Equal($"company:{company}:wallet", await api.Owner.QueryOneAsync<string>("select code from ledger_accounts where company_id = @company", new { company }));
        Assert.Equal(1, await api.CountAsync("select count(*) from audit_log where action = 'WALLET_CREATED' and entity_id = @company", new { company }));
    }

    [Fact]
    public async Task RacingCompanyCreatesWithOneKeyOpenOneWallet()
    {
        // The half of the Java CompanyIdempotencyTests race that tenancy (1C) left to the ledger: one company, one wallet.
        var ops = await id.SignInAsync(await id.SeedAsync(Role.OPS));
        var key = "co-" + Guid.NewGuid();
        var legal = "Race wallet " + Guid.NewGuid();
        var statuses = await Concurrently.RunAsync(4, async _ => (int)(await new LedgerHttp(id).SendAsync("POST", "/api/v1/ops/companies",
            ops.AccessToken, $"{{\"company\":{{\"legalName\":\"{legal}\",\"nameAr\":\"شركة\",\"nameEn\":\"Race\"}},\"lowBalanceThresholdPiastres\":0}}",
            new Dictionary<string, string> { ["Idempotency-Key"] = key })).StatusCode);
        Assert.Single(statuses, s => s == 201);
        Assert.All(statuses, s => Assert.True(s is 201 or 200, $"status {s}"));
        Assert.Equal(1, await api.CountAsync("""
            select count(*) from company_wallets w join companies c on c.id = w.company_id where c.legal_name = @legal
            """, new { legal }));
        Assert.Equal(1, await api.CountAsync("""
            select count(*) from ledger_accounts a join companies c on c.id = a.company_id where c.legal_name = @legal
            """, new { legal }));
    }

    // -------------------------------------------------------------------------------------- payables and reversals

    [Fact]
    public async Task APayableIsOpenedOnFirstUseLockedAfterTheWalletAndAReversalMirrorsOnce()
    {
        // Beyond the Java class (B6's PayoutTests cover the rest with station finance): a fueling-shaped posting moves
        // the wallet and the payable under their locks, each entry records the balance it leaves, and a reversal posts
        // the exact mirror once.
        var company = await id.SeedCompanyAsync();
        var station = await SeedStationAsync();
        await api.PostAsync(SourceType.ADJUSTMENT, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 5_000), Entry.Credit(Account.CompanyWallet(company), 5_000));
        var fueling = await api.PostAsync(SourceType.FUELING, Guid.NewGuid(), Entry.Debit(Account.CompanyWallet(company), 3_000),
            Entry.Credit(Account.StationPayable(station), 3_000));
        Assert.Equal(new PayableChange(station, 0, 3_000), Assert.Single(fueling.Payables));
        Assert.Equal(3_000, await api.Owner.QueryOneAsync<long>("select balance_piastres from station_payables where station_id = @station", new { station }));
        Assert.Equal(3_000, await api.Owner.QueryOneAsync<long>("""
            select e.balance_after_piastres from ledger_entries e join ledger_accounts a on a.id = e.account_id
            where a.code = @code
            """, new { code = Account.StationPayable(station).Code }));

        var reversalId = Guid.NewGuid();
        var reversed = await api.InPlatformAsync(l => l.ReverseAsync(new Reversal(reversalId, fueling.TransactionId, "R-1", "test reversal", Poster.System)));
        Assert.Equal(5_000, await api.CachedBalanceAsync(company));
        Assert.Equal(0, await api.Owner.QueryOneAsync<long>("select balance_piastres from station_payables where station_id = @station", new { station }));
        Assert.Equal(fueling.TransactionId, await api.Owner.QueryOneAsync<Guid>(
            "select reverses_transaction_id from ledger_transactions where id = @id", new { id = reversed.TransactionId }));

        var twice = await Assert.ThrowsAsync<ApiException>(() => api.InPlatformAsync(l =>
            l.ReverseAsync(new Reversal(Guid.NewGuid(), fueling.TransactionId, null, null, Poster.System))));
        Assert.Equal("ALREADY_REVERSED", twice.Code);
        var unknown = await Assert.ThrowsAsync<ApiException>(() => api.InPlatformAsync(l =>
            l.ReverseAsync(new Reversal(Guid.NewGuid(), Guid.NewGuid(), null, null, Poster.System))));
        Assert.Equal(404, unknown.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => api.PostAsync(SourceType.REVERSAL, Guid.NewGuid(), Entry.Debit(Account.PlatformBank(), 1),
            Entry.Credit(Account.CompanyWallet(company), 1)));
        var noStation = await Assert.ThrowsAsync<ApiException>(() => api.InPlatformAsync(l => l.LockPayableAsync(Guid.NewGuid())));
        Assert.Equal(404, noStation.Status);
    }

    [Fact]
    public void WalletsAreLockedInJavasUuidOrder()
    {
        // java.util.UUID.compareTo is signed: 8000… sorts before 1000…, unlike PostgreSQL and .NET.
        var high = Guid.Parse("80000000-0000-0000-0000-000000000000");
        var low = Guid.Parse("10000000-0000-0000-0000-000000000000");
        Assert.True(JavaUuidOrder.Instance.Compare(high, low) < 0);
        Assert.True(JavaUuidOrder.Instance.Compare(low, high) > 0);
        Assert.True(JavaUuidOrder.Instance.Compare(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-8000-000000000000")) > 0);
        Assert.Equal(0, JavaUuidOrder.Instance.Compare(high, high));
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    private Task<long> LowBalanceEventsAsync(Guid company) =>
        api.CountAsync("select count(*) from audit_log where action = 'WALLET_LOW_BALANCE' and entity_id = @company", new { company });

    private async Task<Guid> BankAccountAsync() => await api.Owner.QueryOneAsync<Guid>("select id from ledger_accounts where code = 'platform:bank'");

    private async Task<Guid> WalletAccountAsync(Guid company) =>
        await api.Owner.QueryOneAsync<Guid>("select account_id from company_wallets where company_id = @company", new { company });

    private async Task<Guid> SeedStationAsync()
    {
        var operatorId = await Seed.OperatorAsync(api.Owner);
        var station = Guid.CreateVersion7();
        await api.Owner.ExecuteAsync("""
            insert into stations (id, operator_id, name_ar, name_en, time_zone, connectivity, fuel_types, status, created_at, updated_at)
            values (@station, @operatorId, 'محطة', 'Station', 'Africa/Cairo', 'NORMAL', '{DIESEL}', 'ACTIVE', now(), now())
            """, new { station, operatorId });
        return station;
    }

    private static async Task<Guid> InsertTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid company)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            insert into ledger_transactions (id, source_type, source_id, company_id, posted_at)
            values (@id, 'ADJUSTMENT', @source, @company, now())
            """, connection, tx);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("source", Guid.NewGuid());
        command.Parameters.AddWithValue("company", company);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertEntryAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid txn, Guid account, Guid? company,
        string direction, long amount, long? balanceAfter)
    {
        await using var command = new NpgsqlCommand("""
            insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres,
                                        balance_after_piastres, posted_at)
            values (@id, @txn, @account, @company, @direction, @amount, @after, now())
            """, connection, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("txn", txn);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("company", (object?)company ?? DBNull.Value);
        command.Parameters.AddWithValue("direction", direction);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("after", (object?)balanceAfter ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
