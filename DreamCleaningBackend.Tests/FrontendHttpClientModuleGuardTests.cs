using System.Text.RegularExpressions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// GUARD: no frontend code may use <c>HttpClientModule</c>.
///
/// The app's ONE HttpClient is configured in <c>app.config.ts</c> —
/// <c>provideHttpClient(withFetch(), withInterceptors([authInterceptor]))</c>. A standalone
/// component that lists <c>HttpClientModule</c> in its <c>imports</c> gets a SECOND, private
/// HttpClient with no interceptors, and anything resolved through that component's injector uses
/// it. That is what happened on the booking page (2026-10): its page-level BookingService skipped
/// <c>authInterceptor</c>, so an expired session was never refreshed there — a subscriber silently
/// lost their plan discount and an admin's "book for customer" failed. The same import sat,
/// harmlessly for now, in the auth modal and the login page; all three are gone.
///
/// The check lives in the backend suite because Karma runs in a browser and cannot read source
/// files, and because it must survive the Angular builder change (no webpack require.context).
/// Spec files are exempt: they configure their own TestBed.
/// </summary>
public class FrontendHttpClientModuleGuardTests
{
    private static string FrontendSrc()
    {
        var workspace = Directory.GetParent(SourceTree.TestsProjectDir)!.Parent!.FullName;
        var src = Path.Combine(workspace, "DreamCleaningNG", "src");
        Assert.True(Directory.Exists(src), $"{src} was not found.");
        return src;
    }

    /// <summary>Line and block comments out, so a comment warning against the import is not a hit.</summary>
    private static string StripComments(string source) =>
        Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void NoFrontendCodeImportsHttpClientModule()
    {
        var src = FrontendSrc();
        var offenders = Directory.EnumerateFiles(src, "*.ts", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), @"\bHttpClientModule\b"))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "HttpClientModule gives a component a private HttpClient without the app's interceptors. "
            + "Use the app-level provideHttpClient in app.config.ts instead. Found in: "
            + string.Join(", ", offenders));
    }

    /// <summary>The guard is only worth something if it is actually scanning the components.</summary>
    [Fact]
    public void TheGuardSeesTheComponentsItProtects()
    {
        var src = FrontendSrc();
        foreach (var component in new[]
                 {
                     Path.Combine("app", "booking", "booking.component.ts"),
                     Path.Combine("app", "auth", "auth-modal", "auth-modal.component.ts"),
                     Path.Combine("app", "auth", "login", "login.component.ts")
                 })
            Assert.True(File.Exists(Path.Combine(src, component)), $"{component} was not found.");
    }
}
