namespace Flowbit.Tests;

public static class ExampleWorkflowData
{
    private static readonly string FixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static readonly string ExamplesRoot =
        Path.Combine(FixturesRoot, "examples");

    public static IEnumerable<object[]> All =>
        RelativePaths.Select(path => new object[] { path });

    public static IReadOnlyList<string> RelativePaths =>
        Directory.EnumerateFiles(ExamplesRoot, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(FixturesRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    public static string GetPath(string relativePath) =>
        Path.Combine(
            FixturesRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string Read(string relativePath) =>
        File.ReadAllText(GetPath(relativePath));
}
