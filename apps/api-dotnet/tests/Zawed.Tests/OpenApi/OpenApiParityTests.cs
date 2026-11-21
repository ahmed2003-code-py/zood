using System.Text.Json;
using System.Text.Json.Nodes;
using Zawed.Tests.Support;

namespace Zawed.Tests.OpenApi;

/// <summary>
/// Parity of the HTTP contract, operation by operation, between the Java API's committed OpenAPI document
/// (<c>Fixtures/java-openapi.json</c>) and the document the .NET API generates (ADR 0029, migration plan
/// §"Parity"). For every operation both serve, these must be equal after normalisation:
/// <c>operationId</c>, tags, security, parameters (name, place, required, schema), the request body and every
/// response (status, media type, schema), and every component schema they reach, by name.
/// <para>Normalisation drops what is documentation rather than contract: descriptions, summaries, examples,
/// validation keywords (<c>minLength</c>, <c>pattern</c>, ...), and the <c>"null"</c> member of an OpenAPI 3.1
/// type array (nullability is carried by <c>required</c>).</para>
/// <para>Operations not ported yet are listed in <c>pending-operations.txt</c>; a module agent deletes its
/// lines as it ports them. The test fails on a Java operation that is neither served nor listed, on a
/// listed one that is already served, and on a .NET operation the Java API does not have.</para>
/// </summary>
public sealed class OpenApiParityTests(SharedApi api) : IClassFixture<SharedApi>
{
    private static readonly HashSet<string> Ignored =
    [
        "description", "summary", "example", "examples", "minLength", "maxLength", "pattern", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "uniqueItems", "minItems", "maxItems", "default", "title",
    ];

    [Fact]
    public void TheJavaSnapshotIsTheJavaApisCommittedDocument()
    {
        var java = Path.Combine(Fixtures.RepositoryRoot(), "apps", "api", "openapi", "openapi.json");
        if (File.Exists(java))
        {
            Assert.True(File.ReadAllText(java) == Fixtures.Text("java-openapi.json"),
                "apps/api/openapi/openapi.json changed: copy it to tests/Zawed.Tests/Fixtures/java-openapi.json and port the change");
        }
    }

    [Fact]
    public async Task EveryServedOperationMatchesTheJavaContractAndEveryOtherIsPending()
    {
        var java = JsonNode.Parse(Fixtures.Text("java-openapi.json"))!.AsObject();
        var dotnet = JsonNode.Parse(await api.Client.GetStringAsync("/api/v1/openapi.json"))!.AsObject();
        var pending = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "OpenApi", "pending-operations.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();

        var javaOps = Operations(java);
        var dotnetOps = Operations(dotnet);

        Assert.Empty(dotnetOps.Keys.Where(k => !javaOps.ContainsKey(k)).Select(k => $"served by .NET but not by Java: {k}"));
        Assert.Empty(pending.Where(dotnetOps.ContainsKey).Select(k => $"already served, delete from pending-operations.txt: {k}"));
        Assert.Empty(pending.Where(k => !javaOps.ContainsKey(k)).Select(k => $"not a Java operation, delete from pending-operations.txt: {k}"));
        Assert.Empty(javaOps.Keys.Where(k => !dotnetOps.ContainsKey(k) && !pending.Contains(k)).Select(k => $"neither served nor pending: {k}"));

        var differences = new List<string>();
        var schemas = new HashSet<string>();
        foreach (var key in dotnetOps.Keys.Where(javaOps.ContainsKey).Order())
        {
            var expected = NormaliseOperation(javaOps[key], schemas);
            var actual = NormaliseOperation(dotnetOps[key], schemas);
            if (!JsonNode.DeepEquals(expected, actual))
            {
                differences.Add($"{key}\n  java:   {expected.ToJsonString()}\n  dotnet: {actual.ToJsonString()}");
            }
        }
        foreach (var name in Reachable(java, schemas).Order())
        {
            var expected = Schema(java, name);
            var actual = Schema(dotnet, name);
            if (actual is null)
            {
                differences.Add($"schema {name} is missing from the .NET document");
            }
            else if (!JsonNode.DeepEquals(expected, actual))
            {
                differences.Add($"schema {name}\n  java:   {expected!.ToJsonString()}\n  dotnet: {actual.ToJsonString()}");
            }
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    [Fact]
    public async Task TheDocumentHasTheJavaInfoServersAndSecuritySchemes()
    {
        var java = JsonNode.Parse(Fixtures.Text("java-openapi.json"))!.AsObject();
        var dotnet = JsonNode.Parse(await api.Client.GetStringAsync("/api/v1/openapi.json"))!.AsObject();
        Assert.True(JsonNode.DeepEquals(java["info"], dotnet["info"]), dotnet["info"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(java["servers"], dotnet["servers"]), dotnet["servers"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(java["components"]!["securitySchemes"], dotnet["components"]!["securitySchemes"]),
            dotnet["components"]!["securitySchemes"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(Normalise(java["components"]!["schemas"]!["ApiError"]), Normalise(dotnet["components"]!["schemas"]!["ApiError"])),
            dotnet["components"]!["schemas"]!["ApiError"]!.ToJsonString());
    }

    private static Dictionary<string, JsonObject> Operations(JsonObject document)
    {
        var operations = new Dictionary<string, JsonObject>();
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (method is "get" or "post" or "put" or "patch" or "delete")
                {
                    operations[$"{method.ToUpperInvariant()} {path}"] = operation!.AsObject();
                }
            }
        }
        return operations;
    }

    private static JsonObject NormaliseOperation(JsonObject operation, HashSet<string> schemas)
    {
        var result = new JsonObject
        {
            ["operationId"] = operation["operationId"]?.DeepClone(),
            ["tags"] = operation["tags"]?.DeepClone(),
            ["security"] = operation["security"]?.DeepClone() ?? new JsonArray(),
        };
        var parameters = new JsonArray();
        foreach (var p in (operation["parameters"]?.AsArray() ?? []).OrderBy(p => p!["in"] + ":" + p!["name"]))
        {
            parameters.Add(new JsonObject
            {
                ["name"] = p!["name"]!.DeepClone(), ["in"] = p["in"]!.DeepClone(),
                ["required"] = p["required"]?.GetValue<bool>() ?? false, ["schema"] = Normalise(p["schema"], schemas),
            });
        }
        result["parameters"] = parameters;
        if (operation["requestBody"] is JsonObject body)
        {
            result["requestBody"] = new JsonObject
            {
                ["required"] = body["required"]?.GetValue<bool>() ?? false,
                ["content"] = Content(body["content"], schemas),
            };
        }
        var responses = new JsonObject();
        foreach (var (status, response) in operation["responses"]!.AsObject().OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            responses[status] = Content(response!["content"], schemas);
        }
        result["responses"] = responses;
        return result;
    }

    private static JsonObject Content(JsonNode? content, HashSet<string> schemas)
    {
        var result = new JsonObject();
        foreach (var (type, media) in (content?.AsObject() ?? []).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            result[type] = Normalise(media!["schema"], schemas);
        }
        return result;
    }

    private static JsonNode? Normalise(JsonNode? schema, HashSet<string>? refs = null)
    {
        switch (schema)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (Ignored.Contains(key))
                    {
                        continue;
                    }
                    if (key == "$ref")
                    {
                        var name = value!.GetValue<string>().Split('/')[^1];
                        refs?.Add(name);
                        result["$ref"] = name;
                    }
                    else if (key == "type" && value is JsonArray types)
                    {
                        var kept = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").ToList();
                        result["type"] = kept.Count == 1 ? kept[0] : new JsonArray(kept.Select(t => (JsonNode)t).ToArray());
                    }
                    else if (key == "required" && value is JsonArray required)
                    {
                        result["required"] = new JsonArray(required.Select(r => r!.GetValue<string>()).Order().Select(r => (JsonNode)r).ToArray());
                    }
                    else if (key == "properties" && value is JsonObject properties)
                    {
                        var normalised = new JsonObject();
                        foreach (var (name, property) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                        {
                            normalised[name] = Normalise(property, refs);
                        }
                        result["properties"] = normalised;
                    }
                    else if (key == "additionalProperties")
                    {
                        result["additionalProperties"] = value is JsonValue ? value.DeepClone() : Normalise(value, refs) is JsonObject { Count: 0 } ? true : Normalise(value, refs);
                    }
                    else if (key == "oneOf" && value is JsonArray oneOf && oneOf.Count == 2 && oneOf.Any(o => o?["type"]?.GetValue<string>() == "null"))
                    {
                        // .NET's way of saying "this reference or null": the reference alone, as Java writes it.
                        foreach (var (k, v) in Normalise(oneOf.First(o => o?["type"]?.GetValue<string>() != "null"), refs)!.AsObject())
                        {
                            result[k] = v!.DeepClone();
                        }
                    }
                    else
                    {
                        result[key] = Normalise(value, refs);
                    }
                }
                return result;
            case JsonArray array:
                return new JsonArray(array.Select(a => Normalise(a, refs)).ToArray());
            default:
                return schema?.DeepClone();
        }
    }

    private static JsonNode? Schema(JsonObject document, string name) =>
        document["components"]?["schemas"]?[name] is { } schema ? Normalise(schema) : null;

    private static IEnumerable<string> Reachable(JsonObject java, HashSet<string> roots)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>(roots);
        while (queue.TryDequeue(out var name))
        {
            if (!seen.Add(name))
            {
                continue;
            }
            var refs = new HashSet<string>();
            Normalise(java["components"]?["schemas"]?[name], refs);
            foreach (var r in refs)
            {
                queue.Enqueue(r);
            }
        }
        return seen;
    }
}
