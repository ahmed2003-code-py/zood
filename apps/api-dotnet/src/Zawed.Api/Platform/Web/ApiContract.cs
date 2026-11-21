namespace Zawed.Api.Platform.Web;

/// <summary>
/// The API contract version (Java <c>ApiContract</c>). A client reads it from <c>/api/v1/meta</c>; it
/// increases only on a change an existing client cannot ignore. The .NET API serves contract 1, the
/// same as the Java API: the port changes nothing a client can see.
/// </summary>
public static class ApiContract
{
    public const int Version = 1;
    public const string BasePath = "/api/v1";

    /// <summary>The POS protocol version (B5 contract §1), sent by a terminal as <c>X-Zawed-Contract</c>.</summary>
    public const int PosContract = 1;
    public const int PosContractMinimum = 1;
    public const string PosContractHeader = "X-Zawed-Contract";
}
