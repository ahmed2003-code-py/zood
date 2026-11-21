using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Modules.Notifications;
using Zawed.Api.Platform.Events;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// Every event Java's <c>NotificationTriggers</c> listens to has its handler here (wave 5B review: the payout and bank
/// account triggers were missing and station managers heard of neither). The Java class is read from the repository, so a
/// trigger added there fails this test until it is ported.
/// </summary>
public sealed partial class NotificationTriggerParityTests
{
    [Fact]
    public void EveryJavaTriggerHasItsHandler()
    {
        var java = File.ReadAllText(Path.Combine(Fixtures.RepositoryRoot(), "apps", "api", "src", "main", "java", "com", "zawed", "api",
            "notifications", "NotificationTriggers.java"));
        var javaEvents = JavaListener().Matches(java).Select(m => m.Groups[1].Value).Order().ToList();
        var dotnetEvents = typeof(NotificationTriggers).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>))
            .Select(i => i.GetGenericArguments()[0].Name).Order().ToList();

        Assert.Equal(10, javaEvents.Count);
        Assert.Equal(javaEvents, dotnetEvents);
    }

    [Fact]
    public void EveryHandlerIsRegistered()
    {
        var services = new ServiceCollection();
        new NotificationsModule().AddServices(services, new ConfigurationBuilder().Build());
        var registered = services.Select(d => d.ServiceType).ToHashSet();
        var missing = typeof(NotificationTriggers).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>) && !registered.Contains(i))
            .Select(i => i.GetGenericArguments()[0].Name).ToList();
        Assert.Empty(missing);
    }

    [GeneratedRegex(@"@EventListener\s+void on\((\w+) event\)")]
    private static partial Regex JavaListener();
}
