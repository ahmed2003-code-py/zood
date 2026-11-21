using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Zawed.Api.Modules.Fleet.Contracts;
using Zawed.Api.Platform.Crypto;
using Zawed.Api.Platform.Modules;

namespace Zawed.Api.Modules.Fleet;

/// <summary>Vehicles, RFID tags, limits and bulk imports (Java package <c>fleet</c>). Controllers and EF configurations are discovered.</summary>
public sealed class FleetModule : IZawedModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Vehicles>();
        services.AddScoped<Tags>();
        services.AddScoped<FleetImports>();
        services.AddScoped<ITagGate, FleetTagGate>();
        services.AddScoped<IFleetSnapshot, FleetSnapshotReader>();
        // Tag UIDs, bound exactly as Tags writes and FleetSnapshotReader reads them, for the data-key rotation (ADR 0027).
        services.AddSingleton(new EncryptedColumn("rfid_tags", "uid_encrypted", Tags.AssociatedData));
        services.AddSingleton<FleetImportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<FleetImportWorker>());
        // The upload reads its own file (FleetImportsController.UploadedFileAsync), so MVC sees no body. Document it as the
        // Java document does: springdoc's {file: binary} object, optional, under application/json.
        services.Configure<OpenApiOptions>("v1", options => options.AddOperationTransformer((operation, context, ct) =>
        {
            if (context.Description.ActionDescriptor.AttributeRouteInfo?.Name == "uploadFleetImport")
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                                },
                            },
                        },
                    },
                };
            }
            return Task.CompletedTask;
        }));
    }
}
