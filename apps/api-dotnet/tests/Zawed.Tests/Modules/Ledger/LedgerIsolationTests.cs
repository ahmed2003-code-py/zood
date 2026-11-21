using System.Text.RegularExpressions;
using Npgsql;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Ledger;

/// <summary>
/// Row-level security on the B3 tables, straight against the database as the runtime role (Java
/// <c>LedgerIsolationTests</c>): a platform scope sees all, a company sees its own wallet, entries and top-ups and can
/// only ask and cancel, an operator sees none of it, and nobody can touch the ledger or the reconciliation tables.
/// </summary>
public sealed class LedgerIsolationTests(LedgerApi api) : IClassFixture<LedgerApi>, IAsyncLifetime
{
    private static readonly string[] Tables =
        ["ledger_accounts", "ledger_transactions", "ledger_entries", "company_wallets", "topup_requests", "reconciliation_runs", "reconciliation_findings"];

    private static readonly SemaphoreSlim Seeding = new(1, 1);
    private static Guid companyA;
    private static Guid companyB;

    private Owner Owner => api.Owner;

    public async Task InitializeAsync()
    {
        await Seeding.WaitAsync();
        try
        {
            if (companyA != Guid.Empty)
            {
                return;
            }
            var id = new IdentityTestSupport(api.Factory);
            var a = await id.SeedCompanyAsync();
            var b = await id.SeedCompanyAsync();
            await CreditAsync(a, 7_000);
            await CreditAsync(b, 9_000);
            await Owner.ExecuteAsync("""
                insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,
                                            status, requested_at, created_at, updated_at)
                values (@id, @b, 100, 'ISO-B', @key, current_date, 'PENDING', now(), now(), now())
                """, new { id = Guid.NewGuid(), b, key = "ISOB" + b.ToString("N").ToUpperInvariant() });
            (companyA, companyB) = (a, b);
        }
        finally
        {
            Seeding.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task WithoutAScopeNothingIsVisible()
    {
        await using var app = await AppAsync(null, null);
        foreach (var table in Tables)
        {
            Assert.True(await app.CountAsync(table) == 0, table);
        }
    }

    [Fact]
    public async Task ACompanySeesItsOwnWalletEntriesAndTopUpsOnly()
    {
        await using var app = await AppAsync("COMPANY", companyA);
        Assert.Equal(1, await app.CountAsync("company_wallets"));
        Assert.Equal(1, await app.CountAsync($"company_wallets where company_id = '{companyA}'"));
        Assert.Equal(0, await app.CountAsync($"ledger_entries where company_id = '{companyB}'"));
        Assert.Equal(await app.CountAsync($"ledger_entries where company_id = '{companyA}'"), await app.CountAsync("ledger_entries"));
        Assert.True(await app.CountAsync($"ledger_entries where company_id = '{companyA}'") > 0);
        // The bank side of its own top-up is not its business, nor is the bank account.
        Assert.Equal(0, await app.CountAsync("ledger_entries where company_id is null"));
        Assert.Equal(0, await app.CountAsync("ledger_accounts where code = 'platform:bank'"));
        Assert.Equal(0, await app.CountAsync($"ledger_transactions where company_id <> '{companyA}'"));
        Assert.Equal(0, await app.CountAsync($"topup_requests where company_id = '{companyB}'"));
        Assert.Equal(0, await app.CountAsync("reconciliation_runs"));
        Assert.Equal(0, await app.CountAsync("reconciliation_findings"));
    }

    [Fact]
    public async Task AnOperatorSeesNoneOfIt()
    {
        await using var app = await AppAsync("OPERATOR", Guid.NewGuid());
        foreach (var table in Tables)
        {
            Assert.True(await app.CountAsync(table) == 0, table);
        }
    }

    [Fact]
    public async Task ACompanyCanOnlyAskForATopUpAndWithdrawIt()
    {
        var pending = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            await app.ExecuteAsync(Claim(pending, companyA, user));
            await app.CommitAsync();
        }
        Assert.Contains("row-level security", await RefusalAsync("COMPANY", companyA, user, Claim(Guid.NewGuid(), companyB, user))); // another company's
        // A request born decided: the V12 guard answers before the policy.
        Assert.Contains("starts pending", await RefusalAsync("COMPANY", companyA, user, TopUpInsert(Guid.NewGuid(), companyA, "REJECTED")));
        // Confirming its own request.
        Assert.Matches(new Regex("(row-level security|only a verified top-up is approved|the decider must be the acting user)", RegexOptions.Singleline),
            await RefusalAsync("COMPANY", companyA, user, $"""
                update topup_requests set status = 'CONFIRMED', decided_at = now(), ledger_transaction_id = (
                  select id from ledger_transactions limit 1) where id = '{pending}'
                """));
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            Assert.Equal(1, await app.ExecuteAsync(
                $"update topup_requests set status = 'CANCELLED', decided_by = '{user}', decided_at = now() where id = '{pending}'"));
            await app.CommitAsync();
        }
    }

    [Fact]
    public async Task ACompanyRequestCarriesTheClaimAndNothingElse()
    {
        // Review finding (low, V17): the database refuses a company session's PENDING row that already carries a
        // verification, Operations' origin, another receiving account or another requester.
        var user = Guid.NewGuid();
        var forged = new Dictionary<string, string>
        {
            [$"verified_amount_piastres = 100, verified_by = '{user}', verified_at = now()"] = "a request is not verified",
            ["evidence_reference = 'statement'"] = "a request is not verified",
            ["origin = 'OPS_RECORDED'"] = "a company request",
            ["receiving_account = 'CIB'"] = "receiving account",
            [$"requested_by = '{Guid.NewGuid()}'"] = "the requester must be the acting user",
            ["requested_by = null"] = "the requester must be the acting user",
            [$"decided_by = '{user}'"] = "carries no decision",
            ["decision_reason = 'pre-decided'"] = "carries no decision",
        };
        foreach (var (columns, message) in forged)
        {
            Assert.Contains(message, await RefusalAsync("COMPANY", companyA, user, ClaimWith(Guid.NewGuid(), companyA, user, columns)));
        }
        // The honest claim still goes in.
        await using var app = await AppAsync("COMPANY", companyA, user);
        Assert.Equal(1, await app.ExecuteAsync(Claim(Guid.NewGuid(), companyA, user)));
    }

    [Fact]
    public async Task AWithdrawalChangesNothingButTheStatusAndTheDecision()
    {
        // Review finding (low): the company's role has UPDATE on the whole table (V6), so the statement that withdraws a
        // request could also rewrite it. Each forged column is refused; the decision columns and updated_at may change.
        var user = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            await app.ExecuteAsync(Claim(pending, companyA, user));
            await app.CommitAsync();
        }
        string Withdraw(string set) =>
            $"update topup_requests set status = 'CANCELLED', decided_by = '{user}', decided_at = now(), {set} where id = '{pending}'";
        foreach (var forged in new[]
        {
            $"verified_amount_piastres = 100, verified_by = '{user}', verified_at = now()", "evidence_reference = 'statement line 4'",
            "receiving_account = 'CIB'", $"bank_reference_key = 'OTHERKEY{pending.ToString("N").ToUpperInvariant()}'", "bank_reference = 'OTHER'",
            "note = 'rewritten'", $"ledger_transaction_id = '{Guid.NewGuid()}'", "created_at = now() - interval '1 day'",
        })
        {
            Assert.Contains("a withdrawal changes only the status and the decision", await RefusalAsync("COMPANY", companyA, user, Withdraw(forged)));
        }
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            Assert.Equal(1, await app.ExecuteAsync(Withdraw("decision_reason = 'sent twice', updated_at = now()")));
            await app.CommitAsync();
        }
        Assert.Equal("CANCELLED/MAIN/-", await Owner.QueryOneAsync<string>(
            "select status || '/' || receiving_account || '/' || coalesce(verified_by::text, '-') from topup_requests where id = @pending", new { pending }));
    }

    [Fact]
    public async Task OnlyTheCompanyWithdrawsARequestAndOnlyAsItself()
    {
        // Review finding (low, V17): the V12 guard did not check who cancels.
        var user = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            await app.ExecuteAsync(Claim(pending, companyA, user));
            await app.CommitAsync();
        }
        string CancelAs(string decider) =>
            $"update topup_requests set status = 'CANCELLED', decided_by = {decider}, decided_at = now() where id = '{pending}'";
        Assert.Contains("the decider must be the acting user", await RefusalAsync("COMPANY", companyA, user, CancelAs($"'{Guid.NewGuid()}'"))); // someone else's name
        Assert.Contains("the decider must be the acting user", await RefusalAsync("COMPANY", companyA, user, CancelAs("null"))); // nobody's name
        var opsUser = Guid.NewGuid();
        // Operations rejects with a reason; it never withdraws a company's request.
        Assert.Contains("only the company withdraws", await RefusalAsync("PLATFORM", null, opsUser, CancelAs($"'{opsUser}'")));

        // A verified top-up waiting for approval can never be cancelled, by anyone: there is no such path in TopUps
        // (Operations rejects it, with a reason, audited as TOPUP_REJECTED).
        var awaiting = Guid.NewGuid();
        await WithoutGuardAsync($"""
            insert into topup_requests (id, company_id, origin, amount_piastres, bank_reference, bank_reference_key,
                                        transfer_date, status, requested_at, verified_amount_piastres, verified_by,
                                        verified_at, created_at, updated_at)
            values ('{awaiting}', '{companyA}', 'OPS_RECORDED', 100, 'AWAIT', 'AWAIT{awaiting.ToString("N").ToUpperInvariant()}', current_date,
                    'PENDING_APPROVAL', now(), 100, '{Guid.NewGuid()}', now(), now(), now())
            """);
        string CancelAwaiting(Guid decider) =>
            $"update topup_requests set status = 'CANCELLED', decided_by = '{decider}', decided_at = now() where id = '{awaiting}'";
        Assert.Contains("only a request nobody has verified is withdrawn", await RefusalAsync("PLATFORM", null, opsUser, CancelAwaiting(opsUser)));
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            Assert.Equal(0, await app.ExecuteAsync(CancelAwaiting(user))); // not even visible to the company's update
        }

        // The company's own withdrawal, in its own name, still works.
        await using (var app = await AppAsync("COMPANY", companyA, user))
        {
            Assert.Equal(1, await app.ExecuteAsync(CancelAs($"'{user}'")));
            await app.CommitAsync();
        }
    }

    [Fact]
    public async Task ACompanyCannotWriteTheLedgerOrItsWallet()
    {
        Assert.Contains("row-level security", await RefusalAsync("COMPANY", companyA, null, "update company_wallets set balance_piastres = 1000000"));
        Assert.Contains("row-level security", await RefusalAsync("COMPANY", companyA, null, $"""
            insert into ledger_transactions (id, source_type, source_id, company_id, posted_at)
            values ('{Guid.NewGuid()}', 'ADJUSTMENT', '{Guid.NewGuid()}', '{companyA}', now())
            """));
        Assert.Contains("row-level security", await RefusalAsync("COMPANY", companyA, null, $"""
            insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres, posted_at)
            select '{Guid.NewGuid()}', t.id, w.account_id, '{companyA}', 'CREDIT', 1, now()
            from ledger_transactions t, company_wallets w limit 1
            """));
    }

    [Fact]
    public async Task NothingCanBeDeletedAndTheReconciliationTablesAreReadOnly()
    {
        foreach (var table in Tables)
        {
            Assert.Contains("permission denied", await RefusalAsync("PLATFORM", null, null, "delete from " + table));
        }
        Assert.Contains("permission denied", await RefusalAsync("PLATFORM", null, null,
            $"insert into reconciliation_runs (id, trigger, started_at) values ('{Guid.NewGuid()}', 'MANUAL', now())"));
        Assert.Contains("permission denied", await RefusalAsync("PLATFORM", null, null, "update ledger_transactions set reference = 'x'"));
    }

    [Fact]
    public async Task ADecidedTopUpNeverChangesForAnyRole()
    {
        // B3 review finding 4: reopening a decided request would free its reference to credit again. Since V12 no row can
        // be inserted already decided, not even by the owner; the test plants one with the guard switched off.
        var decided = Guid.NewGuid();
        await WithoutGuardAsync(TopUpInsert(decided, companyA, "REJECTED"));
        var reopen = $"update topup_requests set status = 'CANCELLED', decision_reason = null where id = '{decided}'";
        Assert.Contains("already decided", await RefusalAsync("PLATFORM", null, null, reopen));
        await using (var app = await AppAsync("COMPANY", companyA))
        {
            Assert.Equal(0, await app.ExecuteAsync(reopen)); // since V12 the company's update policy passes only PENDING rows
        }
        Assert.Contains("already decided", (await RawSql.RefusedAsync(() => Owner.ExecuteAsync(reopen))).Message); // the owner too

        var pending = Guid.NewGuid();
        await Owner.ExecuteAsync(TopUpInsert(pending, companyA, "PENDING"));
        Assert.Contains("request facts never change", await RefusalAsync("PLATFORM", null, null,
            $"update topup_requests set amount_piastres = 999999 where id = '{pending}'"));
    }

    [Fact]
    public async Task OnlyAWalletsCachedBalanceCanBeUpdated()
    {
        Assert.Contains("permission denied", await RefusalAsync("PLATFORM", null, null,
            $"update company_wallets set account_id = account_id where company_id = '{companyA}'"));
    }

    [Fact]
    public async Task AReferenceKeyOutsideTheCanonicalShapeIsRefused()
    {
        var refused = await RawSql.RefusedAsync(() => Owner.ExecuteAsync("""
            insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,
                                        status, requested_at, created_at, updated_at)
            values (@id, @companyA, 100, 'ft-1', 'FT-1', current_date, 'PENDING', now(), now(), now())
            """, new { id = Guid.NewGuid(), companyA }));
        Assert.Equal("topup_requests_reference_key_shape", refused.ConstraintName);
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    /// <summary>A balanced top-up posted as the owner, with the cached balance moved alongside.</summary>
    private async Task CreditAsync(Guid company, long piastres)
    {
        var txn = Guid.NewGuid();
        var bank = await Owner.QueryOneAsync<Guid>("select id from ledger_accounts where code = 'platform:bank'");
        var wallet = await Owner.QueryOneAsync<Guid>("select account_id from company_wallets where company_id = @company", new { company });
        await Owner.ExecuteAsync($"""
            begin;
            insert into ledger_transactions (id, source_type, source_id, company_id, posted_at)
            values ('{txn}', 'TOPUP', '{Guid.NewGuid()}', '{company}', now());
            insert into ledger_entries (id, transaction_id, account_id, company_id, direction, amount_piastres, balance_after_piastres, posted_at)
            values ('{Guid.NewGuid()}', '{txn}', '{bank}', null, 'DEBIT', {piastres}, null, now()),
                   ('{Guid.NewGuid()}', '{txn}', '{wallet}', '{company}', 'CREDIT', {piastres}, {piastres}, now());
            update company_wallets set balance_piastres = {piastres} where company_id = '{company}';
            commit;
            """);
    }

    /// <summary>Plants a row the V12 guard would refuse, with the guard off for one transaction only.</summary>
    private Task WithoutGuardAsync(string insert) => Owner.ExecuteAsync($"""
        begin;
        alter table topup_requests disable trigger topup_requests_guard;
        {insert};
        alter table topup_requests enable trigger topup_requests_guard;
        commit;
        """);

    private static string TopUpInsert(Guid id, Guid company, string status) => $"""
        insert into topup_requests (id, company_id, amount_piastres, bank_reference, bank_reference_key, transfer_date,
                                    status, requested_at, decided_at, decision_reason, created_at, updated_at)
        values ('{id}', '{company}', 100, 'REF', 'ISO{id.ToString("N").ToUpperInvariant()}', current_date, '{status}', now(),
                {(status == "PENDING" ? "null" : "now()")}, 'r', now(), now())
        """;

    /// <summary>A company's top-up request exactly as <c>TopUps.RequestAsync</c> writes it.</summary>
    private static string Claim(Guid id, Guid company, Guid user) => ClaimWith(id, company, user, null);

    /// <summary>The same request with columns forced, as <c>"column = sql value, ..."</c>.</summary>
    private static string ClaimWith(Guid id, Guid company, Guid user, string? forced)
    {
        var columns = new Dictionary<string, string>
        {
            ["id"] = $"'{id}'",
            ["company_id"] = $"'{company}'",
            ["origin"] = "'COMPANY_REQUEST'",
            ["amount_piastres"] = "100",
            ["receiving_account"] = "'MAIN'",
            ["bank_reference"] = "'REF'",
            ["bank_reference_key"] = $"'ISO{id.ToString("N").ToUpperInvariant()}'",
            ["transfer_date"] = "current_date",
            ["status"] = "'PENDING'",
            ["requested_by"] = $"'{user}'",
            ["requested_at"] = "now()",
            ["created_at"] = "now()",
            ["updated_at"] = "now()",
        };
        foreach (var assignment in forced?.Split(',') ?? [])
        {
            var pair = assignment.Split('=', 2);
            columns[pair[0].Trim()] = pair[1].Trim();
        }
        return $"insert into topup_requests ({string.Join(", ", columns.Keys)}) values ({string.Join(", ", columns.Values)})";
    }

    private async Task<AppTx> AppAsync(string? scopeType, Guid? scopeId, Guid? userId = null)
    {
        var (connection, tx) = await RawSql.OpenAsync(Owner, true, scopeType, scopeId, userId);
        return new AppTx(connection, tx);
    }

    /// <summary>The refusal's message for one statement as the runtime role, in a transaction of its own.</summary>
    private async Task<string> RefusalAsync(string? scopeType, Guid? scopeId, Guid? userId, string sql)
    {
        await using var app = await AppAsync(scopeType, scopeId, userId);
        return (await RawSql.RefusedAsync(() => app.ExecuteAsync(sql))).Message;
    }

    /// <summary>A transaction as the runtime role, told what <c>DbTx</c> would tell it; rolled back unless committed.</summary>
    private sealed class AppTx(NpgsqlConnection connection, NpgsqlTransaction tx) : IAsyncDisposable
    {
        public Task<int> ExecuteAsync(string sql) => RawSql.ExecuteAsync(connection, tx, sql);

        public Task<long> CountAsync(string from) => RawSql.CountAsync(connection, tx, from);

        public Task CommitAsync() => tx.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await tx.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
