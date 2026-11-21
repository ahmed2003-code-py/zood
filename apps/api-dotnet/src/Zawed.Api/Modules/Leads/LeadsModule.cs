using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Zawed.Api.Platform;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Leads;

/// <summary>Demo requests and the Ops leads list (Java package <c>leads</c>). Controllers are discovered.</summary>
public sealed class LeadsModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Leads>();
        services.AddSingleton<LeadMetrics>();
        services.AddScoped<DemoRequestGate>();
        PlatformSetup.AddJob<LeadRetention>(services);
        // springdoc wrote allowableValues as an enum and the e-mail rule as format: email; the platform's transformer knows
        // only its own attributes, so the module documents its own.
        services.Configure<OpenApiOptions>("v1", options => options.AddSchemaTransformer((schema, context, ct) =>
        {
            if (context.JsonPropertyInfo?.AttributeProvider is PropertyInfo property)
            {
                if (Attribute<AllowableValuesAttribute>(property) is { } allowed)
                {
                    schema.Enum = [.. allowed.Values.Select(v => (JsonNode)JsonValue.Create(v))];
                }
                if (Attribute<DemoEmailAttribute>(property) is not null)
                {
                    schema.Format = "email";
                }
            }
            return Task.CompletedTask;
        }));
    }

    /// <summary>The attribute on the property, or on the positional record parameter it comes from.</summary>
    private static T? Attribute<T>(PropertyInfo property) where T : System.Attribute =>
        property.GetCustomAttribute<T>() ?? property.DeclaringType?.GetConstructors().SelectMany(c => c.GetParameters())
            .Where(p => p.Name == property.Name).Select(p => p.GetCustomAttribute<T>()).FirstOrDefault(a => a is not null);
}
