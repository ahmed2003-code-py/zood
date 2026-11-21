using System.Net.Http.Json;
using System.Text.Json;

namespace Zawed.Tests.Support;

/// <summary>Small helpers for asserting HTTP answers.</summary>
public static class Http
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement;
    }

    public static string? Header(this HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(", ", content) : null;

    public static HttpRequestMessage Request(HttpMethod method, string path, object? json = null, string? rawJson = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        else if (rawJson is not null)
        {
            request.Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>Asserts the ApiError envelope: status, code, request id equal to the header.</summary>
    public static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        var body = await response.JsonAsync();
        Assert.Equal(status, body.GetProperty("statusCode").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(response.Header("X-Request-Id"), body.GetProperty("requestId").GetString());
        return body;
    }
}
