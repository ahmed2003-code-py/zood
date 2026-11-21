using System.Reflection;

namespace Zawed.Api.Platform.Modules;

/// <summary>
/// A business module's registration (the Spring Modulith module's beans). Each module has one
/// implementation in <c>Modules/&lt;Name&gt;/&lt;Name&gt;Module.cs</c>; they are discovered from the assembly, so a
/// module agent never edits <c>Program.cs</c> or another module's files. Controllers and EF configurations
/// are discovered on their own; this registers services, stores and jobs.
/// </summary>
public interface IZawedModule
{
    void AddServices(IServiceCollection services, IConfiguration configuration);
}

public static class ZawedModules
{
    public static IReadOnlyList<IZawedModule> Discover(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IZawedModule).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IZawedModule)Activator.CreateInstance(t)!)
            .ToList();
}
