using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Zawed.Tests.Support;

/// <summary>
/// The application on a real Kestrel socket, for what <c>TestServer</c> and <c>HttpClient</c> would hide:
/// a request target sent exactly as written (<c>HttpClient</c> unescapes <c>%74</c> to <c>t</c> before sending).
/// </summary>
public sealed class KestrelApi : IAsyncLifetime
{
    private readonly ZawedApiFactory factory = new(settings: new Dictionary<string, string?> { ["PORT"] = "0" });

    public ZawedApiFactory Factory => factory;

    public Uri Address { get; private set; } = null!;

    public Task InitializeAsync()
    {
        factory.UseKestrel(0);
        factory.StartServer();
        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Address = new Uri(addresses.Addresses.First().Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1"));
        return Task.CompletedTask;
    }

    /// <summary>Sends one raw HTTP/1.1 request and returns the status line and headers and body as text.</summary>
    public async Task<(int Status, string Response)> RawAsync(string method, string target, string? body = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Address.Host, Address.Port);
        await using var stream = tcp.GetStream();
        var request = new StringBuilder()
            .Append($"{method} {target} HTTP/1.1\r\nHost: {Address.Authority}\r\nConnection: close\r\n");
        if (body is not null)
        {
            request.Append($"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        }
        request.Append("\r\n").Append(body);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToString()));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        return (int.Parse(text.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), text);
    }

    public async Task DisposeAsync() => await factory.DisposeAsync();
}
