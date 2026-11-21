using System.Diagnostics;
using System.Globalization;
using System.Text;
using Zawed.Tests.Modules.Network;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Fueling.FuelingTestSupport;

namespace Zawed.Tests.Modules.Fueling;

/// <summary>
/// An executed load measurement (Java <c>OnlineFuelingLoadMeasurement</c>): concurrent online fuelings through the real HTTP
/// API (device token, agent session, <c>POST /pos/fuelings</c>) on the test PostgreSQL. Opt-in: without
/// <c>ZAWED_PERF=true</c> it returns at once, so the normal suite neither runs nor skips it.
/// <code>ZAWED_PERF=true dotnet test Zawed.slnx --filter "FullyQualifiedName~OnlineFuelingLoadMeasurement"</code>
/// <para><c>ZAWED_PERF_THREADS</c> threads (default 20) each record <c>ZAWED_PERF_PER_THREAD</c> fuelings (default 200), every
/// one on its own vehicle. Three in four go to 100 "spread" companies; one in four to one shared "hot" company, so every
/// thread contends for that company's wallet lock. Each thread alternates between two devices of its own.</para>
/// <para>Afterwards every wallet must equal what was credited minus what was charged and the sum of its ledger entries, the
/// payables together must equal the total charged, and PostgreSQL must report no deadlock. The numbers go to
/// <c>TestResults/perf/load-measurement.md</c> under the test output folder.</para>
/// </summary>
public sealed class OnlineFuelingLoadMeasurement(SharedApi api) : IClassFixture<SharedApi>
{
    private const int SpreadCompanies = 100;
    private const long VolumeMl = 20_000; // 20 L at 15 EGP = 30,000 piastres
    private const long Amount = VolumeMl * Price / 1_000;
    private const long SpreadCredit = 5_000_000;
    private const long HotCredit = 50_000_000;

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool IsHot(int i) => i % 4 == 3;

    [Fact]
    public async Task ConcurrentOnlineFuelingsKeepTheMoneyExact()
    {
        if (Environment.GetEnvironmentVariable("ZAWED_PERF") != "true")
        {
            return; // opt-in measurement
        }
        var threads = Setting("ZAWED_PERF_THREADS", 20);
        var perThread = Setting("ZAWED_PERF_PER_THREAD", 200);
        var f = new FuelingTestSupport(api.Factory);
        var setup = Stopwatch.StartNew();

        var pumps = new List<Pump>();
        for (var p = 0; p < threads * 2; p++)
        {
            f.Id.Limiter.Reset(); // activation is limited per address, and every pump comes from the same one
            var pump = await f.PumpAsync("GAS_92");
            await f.PriceAsync(pump.StationId, "GAS_92", Price);
            pumps.Add(pump);
        }
        var spread = new List<string>();
        for (var c = 0; c < SpreadCompanies; c++)
        {
            f.Id.Limiter.Reset(); // the setup is one Ops user, limited per minute like any user
            var company = await f.CompanyAsync();
            await f.CreditAsync(company, SpreadCredit);
            spread.Add(company);
        }
        var hot = await f.CompanyAsync();
        await f.CreditAsync(hot, HotCredit);
        var cars = new Car[threads, perThread];
        var credited = new Dictionary<string, long>(spread.Select(c => KeyValuePair.Create(c, SpreadCredit))) { [hot] = HotCredit };
        for (var t = 0; t < threads; t++)
        {
            for (var i = 0; i < perThread; i++)
            {
                f.Id.Limiter.Reset();
                cars[t, i] = await f.CarAsync(IsHot(i) ? hot : spread[(t * perThread + i) % SpreadCompanies]);
            }
        }
        var setupSeconds = setup.Elapsed.TotalSeconds;
        var deadlocksBefore = await f.CountAsync("select deadlocks from pg_stat_database where datname = current_database()");

        f.Id.Limiter.Reset();
        var samples = new List<(long Ticks, bool Hot, int Status, string Outcome)>[threads];
        var run = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(async () =>
        {
            samples[t] = [];
            for (var i = 0; i < perThread; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var response = await f.FuelAsync(pumps[t * 2 + i % 2], cars[t, i], VolumeMl);
                var ticks = Stopwatch.GetTimestamp() - started;
                var outcome = (int)response.StatusCode == 201
                    ? (await BodyAsync(response, 201)).GetProperty("status").GetString()!
                    : await CodeAsync(response) ?? "?";
                samples[t].Add((ticks, IsHot(i), (int)response.StatusCode, outcome));
            }
        })));
        var seconds = run.Elapsed.TotalSeconds;
        var all = samples.SelectMany(s => s).ToList();

        Assert.All(all, s => Assert.Equal((201, "COMPLETED"), (s.Status, s.Outcome)));
        var charged = new Dictionary<string, long>();
        for (var t = 0; t < threads; t++)
        {
            for (var i = 0; i < perThread; i++)
            {
                charged[cars[t, i].CompanyId] = charged.GetValueOrDefault(cars[t, i].CompanyId) + Amount;
            }
        }
        foreach (var (company, credit) in credited)
        {
            var balance = await f.WalletBalanceAsync(company);
            Assert.Equal(credit - charged.GetValueOrDefault(company), balance);
            Assert.Equal(balance, await f.CountAsync("""
                select coalesce(sum(case direction when 'CREDIT' then amount_piastres else -amount_piastres end), 0)::bigint
                from ledger_entries where company_id = @c::uuid
                """, new { c = company }));
        }
        long payables = 0;
        foreach (var pump in pumps)
        {
            payables += await f.CountAsync("select coalesce(sum(balance_piastres), 0)::bigint from station_payables where station_id = @s::uuid",
                new { s = pump.StationId });
        }
        Assert.Equal((long)threads * perThread * Amount, payables);
        var deadlocks = await f.CountAsync("select deadlocks from pg_stat_database where datname = current_database()") - deadlocksBefore;
        Assert.Equal(0, deadlocks);

        string Percentiles(IEnumerable<long> ticks)
        {
            var sorted = ticks.Order().ToList();
            string At(int p) => (sorted[Math.Min(sorted.Count - 1, sorted.Count * p / 100)] * 1000 / Stopwatch.Frequency).ToString(CultureInfo.InvariantCulture);
            return $"p50 {At(50)} ms, p95 {At(95)} ms, p99 {At(99)} ms, max {At(100)} ms";
        }
        var report = new StringBuilder("# Online fueling load measurement (.NET)\n\n");
        report.Append(CultureInfo.InvariantCulture, $"- {threads} threads × {perThread} fuelings = {all.Count}, one in four on one hot company\n");
        report.Append(CultureInfo.InvariantCulture, $"- setup {setupSeconds:F1} s, run {seconds:F1} s, {all.Count / seconds:F1} fuelings/s\n");
        report.Append(CultureInfo.InvariantCulture, $"- all: {Percentiles(all.Select(s => s.Ticks))}\n");
        report.Append(CultureInfo.InvariantCulture, $"- hot company: {Percentiles(all.Where(s => s.Hot).Select(s => s.Ticks))}\n");
        report.Append(CultureInfo.InvariantCulture, $"- spread companies: {Percentiles(all.Where(s => !s.Hot).Select(s => s.Ticks))}\n");
        report.Append("- money exact: every wallet, its ledger entries and the payables agree; deadlocks: 0\n");
        var folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "perf");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "load-measurement.md"), report.ToString());
    }
}
