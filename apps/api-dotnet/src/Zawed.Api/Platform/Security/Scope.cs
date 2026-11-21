namespace Zawed.Api.Platform.Security;

/// <summary>Whose data a user works on: the whole platform, one company, or one station operator.</summary>
public enum ScopeType
{
    PLATFORM,
    COMPANY,
    OPERATOR,
}

/// <summary>A data scope. A platform scope has no id; a company or operator scope always has one.</summary>
public sealed record Scope
{
    public Scope(ScopeType type, Guid? id)
    {
        if ((type == ScopeType.PLATFORM) != (id is null))
        {
            throw new ArgumentException($"A {type} scope {(id is null ? "needs" : "has no")} id");
        }
        Type = type;
        Id = id;
    }

    public ScopeType Type { get; }
    public Guid? Id { get; }

    public static readonly Scope Platform = new(ScopeType.PLATFORM, null);

    public static Scope Company(Guid id) => new(ScopeType.COMPANY, id);

    public static Scope Operator(Guid id) => new(ScopeType.OPERATOR, id);

    public bool IsPlatform => Type == ScopeType.PLATFORM;
}
