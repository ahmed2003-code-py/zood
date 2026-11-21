using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zawed.Api.Platform.Web;

/// <summary>
/// The JSON rules of the wire contract, matching the Java API's Jackson settings:
/// <list type="bullet">
/// <item>camelCase properties (the Java record component names);</item>
/// <item>an unknown property in a request body is refused (<c>fail-on-unknown-properties: true</c>);</item>
/// <item>a fraction where a whole number is required (money in piastres, volume in millilitres) is refused
/// as <c>400 VALIDATION_FAILED</c> with <c>INTEGER_REQUIRED</c> on that field (<c>accept-float-as-int: false</c>);</item>
/// <item>enums travel as their declared names, which are the wire names (<c>SUPER_ADMIN</c>, ADR 0029);</item>
/// <item>instants are UTC with a <c>Z</c>, like <c>java.time.Instant</c>.</item>
/// </list>
/// </summary>
public static class ZawedJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.Converters.Add(new WholeNumberConverter<long>(r => r.TryGetInt64(out var v) ? v : null, (w, v) => w.WriteNumberValue(v)));
        options.Converters.Add(new WholeNumberConverter<int>(r => r.TryGetInt32(out var v) ? v : null, (w, v) => w.WriteNumberValue(v)));
        options.Converters.Add(new WholeNumberConverter<short>(r => r.TryGetInt16(out var v) ? v : null, (w, v) => w.WriteNumberValue(v)));
        options.Converters.Add(new UtcInstantConverter());
        options.Converters.Add(new UtcDateTimeConverter());
        return options;
    }
}

/// <summary>
/// Thrown when a JSON number with a fraction or an exponent arrives for a whole-number field. MVC keeps only
/// the message of an input formatter error, so the message is a marker that <see cref="ModelStateErrors"/>
/// recognises; it never reaches a client.
/// </summary>
public sealed class IntegerRequiredException() : JsonException(Marker)
{
    public const string Marker = "ZAWED_INTEGER_REQUIRED";
}

/// <summary>Reads only plain integer literals: <c>100</c>, never <c>100.0</c>, <c>100.5</c> or <c>1e2</c>.</summary>
internal sealed class WholeNumberConverter<T>(Func<Utf8JsonReader, T?> read, Action<Utf8JsonWriter, T> write)
    : JsonConverter<T> where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException($"Expected a number for {typeof(T).Name}");
        }
        var span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
        if (span.Any(b => b is (byte)'.' or (byte)'e' or (byte)'E'))
        {
            throw new IntegerRequiredException();
        }
        return read(reader) ?? throw new JsonException($"The number does not fit in {typeof(T).Name}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => write(writer, value);
}

/// <summary><c>DateTimeOffset</c> written as UTC with <c>Z</c>; any offset accepted when read.</summary>
internal sealed class UtcInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}

/// <summary><c>DateTime</c> is always UTC on the wire; a local or unspecified value is a programming error.</summary>
internal sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("Only UTC times are written (ADR 0007)");
        }
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
