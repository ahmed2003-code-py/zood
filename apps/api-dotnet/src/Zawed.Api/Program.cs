using Zawed.Api.Platform;
using Zawed.Api.Platform.Config;

var builder = WebApplication.CreateBuilder(args);
ZawedConfiguration.Apply(builder.Configuration);
builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["PORT"] ?? "8080"}");
builder.AddZawed();

var app = builder.Build();
await app.PrepareAsync();
app.UseZawed();
await app.RunAsync();

/// <summary>The entry point, visible to <c>WebApplicationFactory</c> in the tests.</summary>
public partial class Program;
