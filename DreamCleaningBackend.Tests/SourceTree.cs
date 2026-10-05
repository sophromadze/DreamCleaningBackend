using System.Runtime.CompilerServices;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Where the test project's SOURCE lives, for the guard tests that read repository files
/// (controllers, services, the frontend's generated copies) rather than compiled types.
///
/// Those tests used to start from <c>AppContext.BaseDirectory</c> and climb until they met a
/// marker folder, which only works while the build output sits inside the repository
/// (<c>DreamCleaningBackend.Tests/bin/...</c>). Built anywhere else — <c>--artifacts-path</c>, a
/// CI output folder, .NET's artifacts layout — they climbed past the repo, or stopped at the
/// output's own <c>bin/DreamCleaningBackend</c> folder, and some fifty tests failed with
/// "file not found". The climb now starts from this file's compile-time location, so it finds
/// the same folders wherever the assemblies end up.
/// </summary>
internal static class SourceTree
{
    /// <summary>The DreamCleaningBackend.Tests project folder (the one holding this file).</summary>
    public static string TestsProjectDir { get; } = Resolve();

    private static string Resolve([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile);
        // A build with PathMap rewrites caller paths; fall back to the old starting point then.
        return dir != null && Directory.Exists(dir) ? dir : AppContext.BaseDirectory;
    }
}
