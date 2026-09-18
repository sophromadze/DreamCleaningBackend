using System.Text.RegularExpressions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// FIRE-AND-FORGET WORK NEVER BORROWS THE CALLER'S DbContext.
///
/// On 2026-09-16 a customer paid $386.16 twice for one cleaning. The first confirm-payment
/// charged the card, created order 369, and then threw
/// "A second operation was started on this context instance before a previous operation
/// completed" — because the confirmation email, the confirmation SMS and the company
/// notification had each been launched with a bare <c>_ = Task.Run(...)</c> that captured the
/// controller's SCOPED IEmailService / ISmsService. Those share the request's
/// ApplicationDbContext, and the first thing either of them does is query it (the blocked-user
/// suppression gate). Three background queries were racing the request. The customer read the
/// error as a decline, pressed Pay again, and got order 370 on a second PaymentIntent.
///
/// The fix is Helpers/BackgroundWork.Run, which gives the work a DI scope — and therefore a
/// DbContext — of its own. This test keeps it a rule rather than a habit: nothing under
/// Controllers/ or Services/ may start detached work any other way.
///
/// Scope of the rule. Controllers and services are exactly where scoped dependencies live, so
/// that is where the mistake is available to make. Hubs/LiveChatHub is deliberately outside it:
/// its one detached block waits 30 seconds and then talks to LiveChatSessionManager and
/// TelegramBotService, both SINGLETONS, and touches no DbContext at all.
/// </summary>
public class BackgroundWorkScopeTests
{
    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "DreamCleaningBackend");
    }

    [Fact]
    public void NoControllerOrServiceStartsDetachedWorkWithABareTaskRun()
    {
        var root = BackendRoot();
        var offenders = new List<string>();

        foreach (var folder in new[] { Path.Combine(root, "Controllers"), Path.Combine(root, "Services") })
        {
            Assert.True(Directory.Exists(folder), $"Expected {folder} to exist");

            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                var code = File.ReadAllText(file);

                // `_ = Task.Run(` is the discard form — fire-and-forget by definition, nobody
                // awaits it, and nothing reports its failure. An AWAITED Task.Run is a different
                // thing entirely (it finishes before the caller moves on) and is not in scope.
                if (Regex.IsMatch(code, @"_\s*=\s*Task\.Run\s*\("))
                    offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Detached work must go through Helpers/BackgroundWork.Run, which gives it a DI scope " +
            "of its own instead of the caller's DbContext. Found a bare fire-and-forget Task.Run in: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void BackgroundWorkOpensItsOwnScope()
    {
        // The whole value of the helper is these two lines. A future edit that resolved a service
        // from anywhere else would put every caller back on the request's DbContext at once.
        var helper = File.ReadAllText(Path.Combine(BackendRoot(), "Helpers", "BackgroundWork.cs"));

        Assert.Contains("scopeFactory.CreateScope()", helper);
        Assert.Contains("work(scope.ServiceProvider)", helper);
    }
}
