using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Zawed.Api.Modules.Leads;
using Zawed.Api.Platform.Web;
using Zawed.Tests.Support;
using static Zawed.Tests.Modules.Leads.DemoFormApi;

namespace Zawed.Tests.Modules.Leads;

/// <summary>
/// Which requests meet the gate (Java <c>DemoRequestGateTests</c>, review of U1). In Java the gate matched the path and a
/// handler-side check refused a demo request the gate had not seen. Here the gate is a filter on the handler itself, so the
/// Java path-matching cases become: the gate is on <c>requestDemo</c> and nowhere else, and a spelling routing does not take
/// as the form's (a capital, a trailing slash) never stores a lead, while one it does take always spends the budget.
/// </summary>
[Collection(DemoFormCollection.Name)]
public sealed class DemoRequestGateTests(DemoFormApi api) : IClassFixture<DemoFormApi>, IAsyncLifetime
{
    public Task InitializeAsync() => api.ForgetCountersAsync();

    public Task DisposeAsync() => api.ForgetCountersAsync();

    [Fact]
    public void TheGateIsOnTheFormsHandlerAndNowhereElse()
    {
        var gated = typeof(DemoRequestGate).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ApiControllerBase)))
            .SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttribute<DemoRequestGateAttribute>() is not null).Select(m => $"{t.Name}.{m.Name}"))
            .ToList();
        Assert.Equal(["LeadsController.RequestDemo"], gated);
        Assert.NotNull(typeof(LeadsController).GetMethod(nameof(LeadsController.RequestDemo))!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task OtherSpellingsAreNoRouteAndNeverStoreALead()
    {
        foreach (var path in new[] { "/api/v1/PUBLIC/demo-requests", "/api/v1/public/demo-requests/", "/api/v1/public/demo-request" })
        {
            var form = Form();
            var response = await api.PostAsync(path, System.Text.Json.JsonSerializer.Serialize(form));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0L, await api.Owner.QueryOneAsync<long>("select count(*) from leads where email = @email", new { email = form["email"] }));
        }
        // None of them met the gate: the address still has its whole budget.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await api.SubmitAsync(Form())).StatusCode);
        }
        Assert.Equal((HttpStatusCode)429, (await api.SubmitAsync(Form())).StatusCode);
    }
}
