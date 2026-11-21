using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Zawed.Api.Platform.Config;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Platform.Meta;

/// <summary>
/// What is running, for clients and for a person checking a deploy (Java <c>MetaController</c>). Public and
/// read-only. It reads the applied schema version from <c>flyway_schema_history</c>, so a 200 here also
/// shows the API reaches its database. <c>version</c> ends in <c>-dotnet</c>, which tells the two APIs apart.
/// </summary>
[Route(ApiContract.BasePath + "/meta")]
[Tags("platform")]
[AllowAnonymous]
public sealed class MetaController(IDbSession db, TimeProvider clock, IOptions<ZawedOptions> options) : ApiControllerBase
{
    public static readonly string BuildVersion =
        typeof(MetaController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    /// <summary>Service name, version, contract version and schema version</summary>
    [HttpGet(Name = "getMeta")]
    [EndpointSummary("Service name, version, contract version and schema version")]
    [ProducesResponseType<MetaResponse>(200)]
    public async Task<MetaResponse> Meta(CancellationToken ct)
    {
        var schemaVersion = await db.Current.QuerySingleOrDefaultAsync<string>(
            "select version from flyway_schema_history where success order by installed_rank desc limit 1", ct: ct);
        return new MetaResponse("zawed-api", BuildVersion, ApiContract.Version,
            options.Value.Environment!.Value.ToString().ToLowerInvariant(), clock.GetUtcNow(), schemaVersion,
            ApiContract.PosContract, ApiContract.PosContractMinimum);
    }
}

/// <param name="ContractVersion">Increases only on a change an existing client cannot ignore</param>
/// <param name="SchemaVersion">Latest applied database migration</param>
/// <param name="PosContract">The POS protocol version this server speaks (header X-Zawed-Contract)</param>
/// <param name="PosContractMinimum">The oldest POS protocol version still accepted</param>
public sealed record MetaResponse(string Service, string Version, int ContractVersion, string Environment, DateTimeOffset ServerTime,
    string? SchemaVersion, int PosContract, int PosContractMinimum);
