using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Leads;

public enum LeadStatus
{
    NEW,
    CONTACTED,
    QUALIFIED,
    CLOSED,
}

/// <summary>
/// What the form sends (Java <c>LeadsController.DemoRequest</c>). The constructor cleans the text first (trim, collapse
/// whitespace, normalise the phone, lower-case the e-mail), as the Java compact constructor did; validation then judges the
/// cleaned values. A class, not a positional record, so the attributes judge the cleaned properties.
/// </summary>
public sealed class DemoRequest
{
    [JsonConstructor]
    public DemoRequest(string? companyName, string? contactName, string? phone, string? email, string? fleetSize, string? locale,
        string? website, long? startedAt)
    {
        CompanyName = DemoRequestText.Collapse(companyName);
        ContactName = DemoRequestText.Collapse(contactName);
        Phone = DemoRequestText.Phone(phone);
        Email = DemoRequestText.EmailOf(email);
        FleetSize = fleetSize;
        Locale = locale;
        Website = website;
        StartedAt = startedAt;
    }

    [NotBlank, Size(2, 120), PlainText]
    public string? CompanyName { get; }

    [NotBlank, Size(2, 80), PlainText]
    public string? ContactName { get; }

    /// <summary>An Egyptian mobile number; +20, 0020, spaces, dashes and Arabic digits are accepted and stored as 01XXXXXXXXX.</summary>
    [NotBlank, Matches(DemoRequestText.EgyptianMobile)]
    public string? Phone { get; }

    [NotBlank, Size(max: 254), DemoEmail, PlainText]
    public string? Email { get; }

    [NotNull, Matches("1-19|20-49|50-99|100-249|250\\+"), AllowableValues("1-19", "20-49", "50-99", "100-249", "250+")]
    public string? FleetSize { get; }

    [NotNull, Matches("ar|en"), AllowableValues("ar", "en")]
    public string? Locale { get; }

    /// <summary>Leave empty. A field people never see; a request that fills it is not stored.</summary>
    public string? Website { get; }

    /// <summary>When the form was shown, epoch milliseconds.</summary>
    [NotNull]
    public long? StartedAt { get; }
}

/// <summary>The same status and body whether the request was stored or set aside.</summary>
public sealed record DemoRequestReceived([AllowableValues("RECEIVED")] string Status)
{
    public static readonly DemoRequestReceived Received = new("RECEIVED");
}

/// <param name="RequestCount">Requests counted on this lead: the first and its repeats within 24 hours.</param>
/// <param name="DifferingRequests">
/// Repeats whose company, contact, phone, e-mail or fleet size differed from what is shown here; the shown details are the
/// first request's.
/// </param>
public sealed record LeadResponse(
    Guid Id,
    string Reference,
    string CompanyName,
    string ContactName,
    string Phone,
    string Email,
    [AllowableValues("1-19", "20-49", "50-99", "100-249", "250+")] string FleetSize,
    [AllowableValues("ar", "en")] string Locale,
    LeadStatus Status,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int RequestCount,
    DateTimeOffset LastRequestedAt,
    int DifferingRequests);

/// <param name="Note">Omitted: unchanged. Blank: removed. Line breaks are kept.</param>
public sealed record LeadUpdateRequest([NotNull] LeadStatus? Status, [Size(max: 1000), NoteText] string? Note);

/// <summary>
/// Java's <c>@Pattern(regexp = PLAIN_TEXT)</c> on a name: no category-C character but the ZWNJ (<see cref="DemoRequestText.IsPlainText"/>),
/// judged by code point. Answers <c>PATTERN</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class PlainTextAttribute() : ValidationAttribute(MatchesAttribute.Message(DemoRequestText.PlainText))
{
    public override bool IsValid(object? value) => value is null || (value is string s && DemoRequestText.IsPlainText(s));
}

/// <summary>Java's <c>@Pattern(regexp = NOTE_TEXT)</c>: the same with line breaks and tabs. Answers <c>PATTERN</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NoteTextAttribute() : ValidationAttribute(MatchesAttribute.Message(DemoRequestText.NoteText))
{
    public override bool IsValid(object? value) => value is null || (value is string s && DemoRequestText.IsNoteText(s));
}

/// <summary>
/// Java's <c>@Email(regexp = EMAIL)</c>: Hibernate's own check (here the platform's <see cref="EmailAttribute"/> and the
/// 64-character local part) and then the expression (<see cref="DemoRequestText.IsEmail"/>). Answers <c>EMAIL</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class DemoEmailAttribute() : ValidationAttribute(Validation.Encode("EMAIL", "must be a well-formed email address"))
{
    private static readonly EmailAttribute Base = new();

    public override bool IsValid(object? value) =>
        value is null || (value is string s && Base.IsValid(s) && s.IndexOf('@') <= 64 && DemoRequestText.IsEmail(s));
}

/// <summary>
/// Jakarta's <c>@Pattern</c> (code <c>PATTERN</c>, the whole value must match), like the platform's <c>[Pattern]</c> but with
/// the expression's braces escaped in the message: <see cref="ValidationAttribute.FormatErrorMessage"/> runs
/// <c>string.Format</c> on it, and <c>[0-9]{8}</c> would throw there (a 500 instead of a 400).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MatchesAttribute(string regexp) : ValidationAttribute(Message(regexp))
{
    private readonly Regex expression = new("^(?:" + regexp + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public override bool IsValid(object? value) => value is null || (value is string s && expression.IsMatch(s));

    internal static string Message(string regexp) =>
        Validation.Encode("PATTERN", $"must match \"{regexp}\"".Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
}

/// <summary>springdoc's <c>allowableValues</c>: the schema of a text with a fixed set of values lists them as an <c>enum</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class AllowableValuesAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}
