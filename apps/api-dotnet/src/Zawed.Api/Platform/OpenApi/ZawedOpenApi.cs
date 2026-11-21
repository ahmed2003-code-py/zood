using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Zawed.Api.Platform.OpenApi;

/// <summary>
/// The API description at <c>/api/v1/openapi.json</c>, shaped like the Java one (springdoc plus
/// <c>OpenApiConfig</c>), because the web app generates its TypeScript types from it and the parity test
/// compares the two operation by operation:
/// <list type="bullet">
/// <item>title "ZAWED API", version <c>v1</c>, one server <c>/</c>, the same description;</item>
/// <item>the three security schemes (<c>userToken</c>, <c>deviceToken</c>, <c>agentSession</c>), and on each
/// operation the requirement the Java document gives it, from the same path rules;</item>
/// <item><c>ApiError</c> (and <c>FieldIssue</c>) in the components, and a <c>default</c> response "Any refusal
/// or failure" with it on every operation;</item>
/// <item>a property is <c>required</c> when it cannot be null, as the Java document marks it.</item>
/// </list>
/// </summary>
public static class ZawedOpenApi
{
    public const string DocumentPath = "/api/v1/openapi.json";
    public const string UserToken = "userToken";
    public const string DeviceToken = "deviceToken";
    public const string AgentSession = "agentSession";

    private const string Description =
        "Fleet fuel management platform. Money is integer piastres, fuel volume is integer millilitres, timestamps are ISO-8601 UTC. "
        + "Every error has the ApiError shape, with a stable `code`.\n\n**Trying it here.** Sign in with POST /api/v1/auth/login (every role, "
        + "Super Admin included), finish two-step verification with /api/v1/auth/mfa/verify where asked, then press **Authorize** and paste "
        + "the `accessToken` into `userToken`. A POS route needs a device token (`deviceToken`) and, for fuelings, the agent's session "
        + "(`agentSession`) instead. Lists are paginated with `cursor` and `limit`.";

    /// <summary>Calls that need no credential (Java <c>OpenApiConfig.PUBLIC</c>).</summary>
    public static readonly IReadOnlySet<string> Public = new HashSet<string>
    {
        "/api/v1/health", "/api/v1/health/liveness", "/api/v1/health/readiness", "/api/v1/meta", "/api/v1/openapi.json",
        "/api/v1/receipts/{id}", "/api/v1/auth/login", "/api/v1/auth/mfa/verify", "/api/v1/auth/mfa/enroll/start",
        "/api/v1/auth/mfa/enroll/confirm", "/api/v1/auth/refresh", "/api/v1/auth/logout", "/api/v1/setup/super-admin",
        "/api/v1/pos/activate", "/api/v1/pos/token", "/api/v1/public/demo-requests",
    };

    /// <summary>POS calls made by the device alone (Java <c>OpenApiConfig.DEVICE_ONLY</c>).</summary>
    public static readonly IReadOnlySet<string> DeviceOnly = new HashSet<string>
    {
        "/api/v1/pos/agents/login", "/api/v1/pos/snapshot", "/api/v1/pos/sync",
    };

    public static IServiceCollection AddZawedOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.CreateSchemaReferenceId = SchemaName;
            options.AddSchemaTransformer(LikeJava);
            options.AddDocumentTransformer(Shape);
        });
        return services;
    }

    private static async Task Shape(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo { Title = "ZAWED API", Version = "v1", Description = Description };
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [UserToken] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "A user's access token: `accessToken` from POST /api/v1/auth/login (every role), after /api/v1/auth/mfa/verify "
                    + "for roles with two-step verification. Valid 15 minutes.",
            },
            [DeviceToken] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "A POS device token from POST /api/v1/pos/token (signed with the device's own key, ADR 0010). Valid 15 minutes. "
                    + "A user token is never accepted here.",
            },
            [AgentSession] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Agent-Session",
                Description = "The agent's session from POST /api/v1/pos/agents/login, sent with the device token.",
            },
        };
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        var apiError = await context.GetOrCreateSchemaAsync(typeof(Web.ApiError), null, ct);
        apiError.Description = "The shape of every refusal and failure";
        document.Components.Schemas["FieldIssue"] = await context.GetOrCreateSchemaAsync(typeof(Web.FieldIssue), null, ct);
        if (apiError.Properties?["fields"] is OpenApiSchema fields)
        {
            fields.Items = new OpenApiSchemaReference("FieldIssue", document);
        }
        document.Components.Schemas["ApiError"] = apiError;

        foreach (var (path, item) in document.Paths)
        {
            if (item.Operations is null)
            {
                continue;
            }
            foreach (var operation in item.Operations.Values)
            {
                operation.Security = Security(path, document);
                if (operation.RequestBody?.Content is { } bodies)
                {
                    // MVC lists every JSON media type its formatter reads; springdoc documents application/json only.
                    foreach (var type in bodies.Keys.Where(t => t != "application/json").ToList())
                    {
                        bodies.Remove(type);
                    }
                }
                operation.Responses ??= new OpenApiResponses();
                foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                {
                    // A 204 has no body: no media type, as springdoc writes it.
                    if (response.Content is { } media && media.Values.All(m => m.Schema is null))
                    {
                        response.Content = null;
                    }
                }
                operation.Responses["default"] = new OpenApiResponse
                {
                    Description = "Any refusal or failure",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = new OpenApiSchemaReference("ApiError", document) },
                    },
                };
            }
        }
    }

    private static List<OpenApiSecurityRequirement> Security(string path, OpenApiDocument document)
    {
        OpenApiSecurityRequirement Requirement(params string[] schemes)
        {
            var requirement = new OpenApiSecurityRequirement();
            foreach (var scheme in schemes)
            {
                requirement[new OpenApiSecuritySchemeReference(scheme, document)] = [];
            }
            return requirement;
        }
        if (Public.Contains(path))
        {
            return [];
        }
        if (DeviceOnly.Contains(path))
        {
            return [Requirement(DeviceToken)];
        }
        return path.StartsWith("/api/v1/pos/", StringComparison.Ordinal) ? [Requirement(DeviceToken, AgentSession)] : [Requirement(UserToken)];
    }

    /// <summary>
    /// Component names as springdoc gives them: an enum is written inline where it is used (springdoc's default), and
    /// a generic type is its name followed by its arguments' (<c>Page&lt;CompanyResponse&gt;</c> is
    /// <c>PageCompanyResponse</c>, not .NET's <c>PageOfCompanyResponse</c>).
    /// </summary>
    private static string? SchemaName(System.Text.Json.Serialization.Metadata.JsonTypeInfo type)
    {
        var clr = Nullable.GetUnderlyingType(type.Type) ?? type.Type;
        if (clr.IsEnum)
        {
            return null;
        }
        if (clr.IsGenericType && type.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
        {
            return clr.Name[..clr.Name.IndexOf('`')] + string.Concat(clr.GetGenericArguments().Select(a => a.Name));
        }
        return OpenApiOptions.CreateDefaultSchemaReferenceId(type);
    }

    /// <summary>
    /// Schemas in springdoc's terms: whole numbers and instants keep their JSON type although they have
    /// ZAWED's own converters; no <c>additionalProperties: false</c> (the unknown-property rule is enforced,
    /// not documented, as in Java); a map has <c>additionalProperties: {}</c>; and <c>required</c> is exactly
    /// the properties that cannot be null.
    /// </summary>
    private static Task LikeJava(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var clr = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (clr.IsEnum)
        {
            // springdoc: {"type": "string", "enum": [names]}, the null of a nullable enum carried by "required" instead.
            schema.Type = JsonSchemaType.String;
            schema.Enum = Enum.GetNames(clr).Select(n => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(n)).ToList();
        }
        else if (clr == typeof(long))
        {
            (schema.Type, schema.Format) = (JsonSchemaType.Integer, "int64");
        }
        else if (clr == typeof(int) || clr == typeof(short))
        {
            (schema.Type, schema.Format) = (JsonSchemaType.Integer, "int32");
        }
        else if (clr == typeof(DateTimeOffset) || clr == typeof(DateTime))
        {
            (schema.Type, schema.Format) = (JsonSchemaType.String, "date-time");
        }
        schema.AdditionalPropertiesAllowed = true;
        if (context.JsonTypeInfo.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary && schema.AdditionalProperties is null)
        {
            schema.AdditionalProperties = new OpenApiSchema();
        }
        // An object type first reached through a property is still its own component, so the rule applies there too.
        if (schema.Properties is null || schema.Properties.Count == 0)
        {
            return Task.CompletedTask;
        }
        var nullability = new NullabilityInfoContext();
        var required = new HashSet<string>();
        foreach (var property in context.JsonTypeInfo.Properties)
        {
            if (property.AttributeProvider is not PropertyInfo info || !schema.Properties.ContainsKey(property.Name))
            {
                continue;
            }
            var nullable = info.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(info.PropertyType) is not null
                : nullability.Create(info).ReadState != NullabilityState.NotNull;
            if (!nullable || DeclaredRequired(info))
            {
                required.Add(property.Name);
            }
        }
        schema.Required = required.Count == 0 ? null : required;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A request property that is nullable in C# only so that its absence reaches validation (<c>[NotNull] Role? Role</c>)
    /// is still required, as springdoc marks a <c>@NotNull</c> or <c>@NotBlank</c> field. On a positional record the
    /// attribute sits on the constructor parameter.
    /// </summary>
    private static bool DeclaredRequired(PropertyInfo property)
    {
        static bool Marks(IEnumerable<object> attributes) =>
            attributes.Any(a => a is Web.NotNullAttribute or Web.NotBlankAttribute or Web.NotEmptyAttribute);
        if (Marks(property.GetCustomAttributes(true)))
        {
            return true;
        }
        var parameter = property.DeclaringType?.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal));
        return parameter is not null && Marks(parameter.GetCustomAttributes(true));
    }
}
