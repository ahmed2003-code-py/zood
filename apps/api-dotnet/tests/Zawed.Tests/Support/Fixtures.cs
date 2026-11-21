namespace Zawed.Tests.Support;

/// <summary>Paths of the committed fixtures (see <c>Fixtures/README.md</c>) and of the repository.</summary>
public static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    /// <summary>The repository root (the folder holding <c>apps/</c>), found by walking up from the test binaries.</summary>
    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "apps", "api-dotnet")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found");
    }
}
