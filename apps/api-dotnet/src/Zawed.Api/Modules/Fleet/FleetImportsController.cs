using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Zawed.Api.Platform.Security;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// Bulk vehicle import and company onboarding readiness in the Ops console (ADR 0026; Java <c>FleetImportsController</c>).
/// The upload reads its file itself, after the permission check, so a caller without the permission is told 403, not
/// "file missing" (Spring's lazy multipart resolution): the action has no file parameter for MVC to bind first.
/// </summary>
[Route("api/v1/ops"), Tags("fleet onboarding")]
public sealed class FleetImportsController(FleetImports imports) : ApiControllerBase
{
    /// <summary>Spring's <c>max-request-size</c>: the whole multipart body, the file part and its headers.</summary>
    private const long MaxRequestBytes = 6L * 1024 * 1024;

    private ZawedPrincipal Actor => ZawedPrincipal.From(User)!;

    /// <summary>The vehicle import template, CSV or XLSX, in Arabic or English.</summary>
    [HttpGet("fleet-imports/template", Name = "downloadFleetImportTemplate"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<byte[]>(200)]
    public IActionResult Template([FromQuery] string format = "xlsx", [FromQuery] string lang = "ar")
    {
        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        var arabic = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var name = "zawed-vehicles-template-" + (arabic ? "ar" : "en") + (csv ? ".csv" : ".xlsx");
        return Download(FleetImports.Template(csv ? ImportFormat.CSV : ImportFormat.XLSX, arabic), name,
            csv ? "text/csv;charset=UTF-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    /// <summary>
    /// Upload a company's vehicle file (CSV or XLSX, at most 5 MB and 5,000 rows); validates every row and creates nothing yet.
    /// </summary>
    [HttpPost("companies/{id}/fleet-imports", Name = "uploadFleetImport"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<FleetImportResponse>(200)]
    public async Task<IActionResult> Upload(Guid id, CancellationToken ct)
    {
        var file = await UploadedFileAsync(ct);
        if (file is null || file.Length == 0)
        {
            return From(Result.Fail(400, "IMPORT_FILE_REQUIRED", "Attach the file as 'file'", "file"));
        }
        if (file.Length > FleetImportFile.MaxBytes)
        {
            // Spring's max-file-size refused such a part before the controller ran: the generic 413, not the import's own.
            throw TooLarge();
        }
        using var content = new MemoryStream((int)file.Length);
        await file.CopyToAsync(content, ct);
        return From(await imports.UploadAsync(Actor, id, file.FileName, content.ToArray(), ct));
    }

    /// <summary>Imports, newest first; filter by company.</summary>
    [HttpGet("fleet-imports", Name = "listFleetImports"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<Page<FleetImportResponse>>(200)]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct) =>
        From(await imports.ListAsync(companyId, cursor, limit, ct));

    /// <summary>One import: its state and counts (poll it while it runs).</summary>
    [HttpGet("fleet-imports/{id}", Name = "getFleetImport"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<FleetImportResponse>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await imports.GetAsync(id, ct));

    /// <summary>An import's rows in file order, with errors; problemsOnly for the preview.</summary>
    [HttpGet("fleet-imports/{id}/rows", Name = "listFleetImportRows"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<FleetImportRowsResponse>(200)]
    public async Task<IActionResult> Rows(Guid id, [FromQuery] ImportRowStatus? status, [FromQuery] bool problemsOnly = false,
        [FromQuery] int after = 0, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        From(await imports.RowsAsync(id, status, problemsOnly, after, limit, ct));

    /// <summary>The rows that need attention, as CSV.</summary>
    [HttpGet("fleet-imports/{id}/errors.csv", Name = "downloadFleetImportErrors"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<byte[]>(200)]
    public async Task<IActionResult> Errors(Guid id, CancellationToken ct)
    {
        var report = await imports.ErrorReportAsync(id, ct);
        return report.IsSuccess
            ? Download(report.Data!, "zawed-import-" + id.ToString()[..8] + "-errors.csv", "text/csv;charset=UTF-8")
            : From(report);
    }

    /// <summary>Queue a validated import; the vehicles are created in the background.</summary>
    [HttpPost("fleet-imports/{id}/confirm", Name = "confirmFleetImport"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<FleetImportResponse>(200)]
    public async Task<IActionResult> Confirm(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConfirmFleetImportRequest? body,
        CancellationToken ct) =>
        From(await imports.ConfirmAsync(Actor, id, body?.Mode, ct));

    /// <summary>Drop an import that was never confirmed.</summary>
    [HttpPost("fleet-imports/{id}/cancel", Name = "cancelFleetImport"), RequiresPermission(Permission.FLEET_MANAGE)]
    [ProducesResponseType<FleetImportResponse>(200)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => From(await imports.CancelAsync(Actor, id, ct));

    /// <summary>Whether a company is ready to fuel, derived from its data: details, policy, vehicles, tags, wallet, users.</summary>
    [HttpGet("companies/{id}/onboarding", Name = "getCompanyOnboarding"), RequiresPermission(Permission.OPS_READ)]
    [ProducesResponseType<OnboardingResponse>(200)]
    public async Task<IActionResult> Onboarding(Guid id, CancellationToken ct) => From(await imports.OnboardingAsync(id, ct));

    /// <summary>
    /// The optional <c>file</c> part of a multipart body, read here and not by model binding. A file part over 5 MB or a
    /// body over 6 MB is <c>413</c> (Spring's <c>MaxUploadSizeExceededException</c>), a broken body <c>400</c>.
    /// </summary>
    private async Task<IFormFile?> UploadedFileAsync(CancellationToken ct)
    {
        if (!Request.HasFormContentType)
        {
            return null;
        }
        if (Request.ContentLength > MaxRequestBytes)
        {
            throw TooLarge();
        }
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxRequestBytes;
        }
        try
        {
            var form = await Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = FleetImportFile.MaxBytes }, ct);
            return form.Files.GetFile("file");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            throw TooLarge();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException && ex is not BadHttpRequestException)
        {
            throw new ApiException(400, "BAD_REQUEST", "Bad Request");
        }
    }

    private static ApiException TooLarge() => new(413, "PAYLOAD_TOO_LARGE", "Content Too Large");

    private FileContentResult Download(byte[] body, string name, string type)
    {
        Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        Response.Headers.CacheControl = "no-store";
        return File(body, type);
    }
}
