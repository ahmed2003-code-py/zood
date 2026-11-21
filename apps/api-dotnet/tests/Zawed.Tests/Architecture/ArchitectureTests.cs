using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Zawed.Tests.Architecture;

/// <summary>
/// The rules the Java build enforced with Spring Modulith (<c>ModularityTests</c>) and <c>ArchitectureTests</c>,
/// checked on the compiled IL with Mono.Cecil (every type, field, signature and method body):
/// <list type="bullet">
/// <item>module boundaries: a module reaches another only through its <c>Contracts</c> namespace, and module
/// dependencies have no cycle; the platform never depends on a module; the domain depends on nothing;</item>
/// <item>reviewed lists that only grow with a review: scope escalations, endpoints without a permission,
/// readers of the raw request path, background services outside the job wrapper;</item>
/// <item>invariant 1 (no floating point for money or volume) and ADR 0007 (the injected clock is the only "now").</item>
/// </list>
/// A failure names the offending type and member; fix the code or, after a review, extend the list with the reason.
/// </summary>
public sealed class ArchitectureTests
{
    private const string ModulesNamespace = "Zawed.Api.Modules.";
    private const string PlatformNamespace = "Zawed.Api.Platform";

    private static readonly ModuleDefinition Api = Load("Zawed.Api.dll");
    private static readonly ModuleDefinition Domain = Load("Zawed.Domain.dll");

    /// <summary>
    /// Who may build a database scope that does not come from the caller's token, and why (Java
    /// <c>ArchitectureTests.ESCALATIONS</c> for <c>DbScope.runAs</c>). Add a type only with its reason, after review.
    /// </summary>
    private static readonly Dictionary<string, string> Escalations = new()
    {
        // (none yet: the identity, network, fueling, sync and fleet-import ports add theirs, each reviewed)
    };

    /// <summary>
    /// Actions that need a signed-in user but no permission (any role), by "Controller.Action". Everything else
    /// declares <c>[AllowAnonymous]</c> or <c>[RequiresPermission]</c>.
    /// </summary>
    private static readonly HashSet<string> SignedInOnly =
    [
    ];

    /// <summary>Types that read the raw request target (ADR 0012 amendment: path matching).</summary>
    private static readonly HashSet<string> RawPathReaders = ["Zawed.Api.Platform.Web.CanonicalPath"];

    /// <summary>Types that may read the system clock or make a random id, and why (ADR 0007).</summary>
    private static readonly Dictionary<string, string> ClockReaders = new()
    {
        ["Zawed.Api.Platform.Web.RequestIds"] = "a request id is a correlation token, not a record id",
    };

    /// <summary>Hosted services that are not a <c>ScheduledJob</c> (and so leave no job_runs record).</summary>
    private static readonly HashSet<string> UnrecordedBackgroundServices = [];

    private static ModuleDefinition Load(string file) =>
        ModuleDefinition.ReadModule(Path.Combine(AppContext.BaseDirectory, file));

    private static IEnumerable<TypeDefinition> Types(ModuleDefinition module) =>
        module.Types.SelectMany(t => t.NestedTypes.Prepend(t)).Where(t => t.Name != "<Module>");

    /// <summary>Every type a type refers to: base, interfaces, fields, signatures, attributes and method bodies.</summary>
    private static IEnumerable<TypeReference> References(TypeDefinition type)
    {
        if (type.BaseType is not null) yield return type.BaseType;
        foreach (var i in type.Interfaces) yield return i.InterfaceType;
        foreach (var a in type.CustomAttributes) yield return a.AttributeType;
        foreach (var f in type.Fields) yield return f.FieldType;
        foreach (var p in type.Properties) yield return p.PropertyType;
        foreach (var m in type.Methods)
        {
            yield return m.ReturnType;
            foreach (var p in m.Parameters) yield return p.ParameterType;
            foreach (var a in m.CustomAttributes) yield return a.AttributeType;
            if (!m.HasBody) continue;
            foreach (var v in m.Body.Variables) yield return v.VariableType;
            foreach (var instruction in m.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case TypeReference t: yield return t; break;
                    case MethodReference mr: yield return mr.DeclaringType; yield return mr.ReturnType; break;
                    case FieldReference fr: yield return fr.DeclaringType; yield return fr.FieldType; break;
                }
            }
        }
    }

    private static IEnumerable<(MethodDefinition Method, MethodReference Called)> Calls(TypeDefinition type) =>
        type.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Where(i => i.OpCode.FlowControl == FlowControl.Call && i.Operand is MethodReference)
                .Select(i => (m, (MethodReference)i.Operand)));

    private static string Root(TypeDefinition type)
    {
        // Compiler-generated nested types (closures, state machines) belong to their declaring type.
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type.FullName;
    }

    private static string? ModuleOf(string? @namespace) =>
        @namespace is not null && @namespace.StartsWith(ModulesNamespace, StringComparison.Ordinal)
            ? @namespace[ModulesNamespace.Length..].Split('.')[0]
            : null;

    private static string NamespaceOf(TypeReference type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type.Namespace;
    }

    [Fact]
    public void ModulesReachEachOtherOnlyThroughTheirContractsAndWithoutCycles()
    {
        var violations = new List<string>();
        var edges = new HashSet<(string From, string To)>();
        foreach (var type in Types(Api))
        {
            var from = ModuleOf(NamespaceOf(type));
            if (from is null) continue;
            foreach (var reference in References(type).Where(r => r is not null))
            {
                var ns = NamespaceOf(reference.GetElementType());
                var to = ModuleOf(ns);
                if (to is null || to == from) continue;
                edges.Add((from, to));
                if (!ns.StartsWith($"{ModulesNamespace}{to}.Contracts", StringComparison.Ordinal))
                {
                    violations.Add($"{Root(type)} ({from}) uses {reference.FullName}, an internal of {to}");
                }
            }
        }
        Assert.Empty(violations.Distinct());
        Assert.Null(Cycle(edges));
    }

    private static string? Cycle(HashSet<(string From, string To)> edges)
    {
        var graph = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());
        var state = new Dictionary<string, int>();
        string? Visit(string node, string path)
        {
            if (state.GetValueOrDefault(node) == 1) return path;
            if (state.GetValueOrDefault(node) == 2) return null;
            state[node] = 1;
            foreach (var next in graph.GetValueOrDefault(node, []))
            {
                if (Visit(next, $"{path} -> {next}") is { } cycle) return cycle;
            }
            state[node] = 2;
            return null;
        }
        return graph.Keys.Select(k => Visit(k, k)).FirstOrDefault(c => c is not null);
    }

    [Fact]
    public void ThePlatformDependsOnNoModule()
    {
        var violations = Types(Api)
            .Where(t => NamespaceOf(t).StartsWith(PlatformNamespace, StringComparison.Ordinal))
            .SelectMany(t => References(t).Where(r => r is not null && ModuleOf(NamespaceOf(r.GetElementType())) is not null)
                .Select(r => $"{Root(t)} uses {r.FullName}"))
            .Distinct();
        Assert.Empty(violations);
    }

    [Fact]
    public void TheDomainDependsOnNothingButTheBaseLibrary()
    {
        var allowed = new[] { "System.Runtime", "System.Private.CoreLib", "netstandard", "System.Collections", "System.Linq", "System.Memory" };
        var others = Domain.AssemblyReferences.Select(a => a.Name).Where(n => !allowed.Contains(n)).ToList();
        Assert.True(others.Count == 0, "Zawed.Domain references " + string.Join(", ", others));
    }

    [Fact]
    public void ScopeEscalationsAreTheReviewedOnesOnly()
    {
        var callers = Types(Api)
            .Where(t => Calls(t).Any(c => c.Called.DeclaringType.FullName == "Zawed.Api.Platform.Persistence.DbScopeContext"
                && c.Called.Name == "Escalate"))
            .Select(Root)
            .Where(n => !n.StartsWith(PlatformNamespace + ".Persistence", StringComparison.Ordinal))
            .ToHashSet();
        Assert.Equal(Escalations.Keys.Order(), callers.Order());
    }

    [Fact]
    public void EveryEndpointIsPublicOnPurposeOrNamesItsPermission()
    {
        var missing = new List<string>();
        foreach (var type in Types(Api).Where(t => t.BaseType?.FullName == "Zawed.Api.Platform.Web.ApiControllerBase"))
        {
            bool Has(Mono.Collections.Generic.Collection<CustomAttribute> attributes, string name) =>
                attributes.Any(a => a.AttributeType.Name == name);
            var classLevel = Has(type.CustomAttributes, "AllowAnonymousAttribute") || Has(type.CustomAttributes, "RequiresPermissionAttribute");
            foreach (var action in type.Methods.Where(m => m.IsPublic && !m.IsConstructor && !m.IsStatic
                && m.CustomAttributes.Any(a => a.AttributeType.Name.StartsWith("Http", StringComparison.Ordinal))))
            {
                var declared = classLevel || Has(action.CustomAttributes, "AllowAnonymousAttribute")
                    || Has(action.CustomAttributes, "RequiresPermissionAttribute");
                if (!declared && !SignedInOnly.Contains($"{type.Name}.{action.Name}"))
                {
                    missing.Add($"{type.Name}.{action.Name}");
                }
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void OnlyTheReviewedTypesReadTheRawRequestTarget()
    {
        var readers = Types(Api)
            .Where(t => Calls(t).Any(c => c.Called.Name == "get_RawTarget"))
            .Select(Root).ToHashSet();
        Assert.Equal(RawPathReaders.Order(), readers.Order());
    }

    [Fact]
    public void TheInjectedClockIsTheOnlyNow()
    {
        string[] forbidden = ["System.DateTime::get_Now", "System.DateTime::get_UtcNow", "System.DateTimeOffset::get_Now",
            "System.DateTimeOffset::get_UtcNow", "System.Guid::NewGuid"];
        var readers = Types(Api).Concat(Types(Domain))
            .Where(t => Calls(t).Any(c => forbidden.Contains($"{c.Called.DeclaringType.FullName}::{c.Called.Name}")))
            .Select(Root).ToHashSet();
        Assert.Equal(ClockReaders.Keys.Order(), readers.Order());
    }

    [Fact]
    public void NoFloatingPointAnywhereInTheApplicationsTypes()
    {
        string[] floating = ["System.Single", "System.Double", "System.Decimal"];
        var offenders = Types(Api).Concat(Types(Domain))
            .SelectMany(t => t.Fields.Select(f => (t, f.Name, f.FieldType)).Concat(t.Properties.Select(p => (t, p.Name, p.PropertyType))))
            .Where(x => floating.Contains(x.Item3.FullName) || x.Item3 is GenericInstanceType g && g.GenericArguments.Any(a => floating.Contains(a.FullName)))
            .Select(x => $"{x.t.FullName}.{x.Name}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryBackgroundServiceIsARecordedJob()
    {
        var unrecorded = Types(Api)
            .Where(t => !t.IsAbstract && Inherits(t, "Microsoft.Extensions.Hosting.BackgroundService")
                && !Inherits(t, "Zawed.Api.Platform.Jobs.ScheduledJob"))
            .Select(t => t.FullName).ToHashSet();
        Assert.Equal(UnrecordedBackgroundServices.Order(), unrecorded.Order());
    }

    private static bool Inherits(TypeDefinition type, string baseName)
    {
        for (var current = type.BaseType; current is not null;)
        {
            if (current.FullName == baseName) return true;
            try
            {
                current = current.Resolve()?.BaseType;
            }
            catch (AssemblyResolutionException)
            {
                return false;
            }
        }
        return false;
    }
}
