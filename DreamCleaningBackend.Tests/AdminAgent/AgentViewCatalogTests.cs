using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using DreamCleaningBackend.Services.AdminAgent;
using Xunit;

namespace DreamCleaningBackend.Tests.AdminAgent;

/// <summary>
/// The agent_* views (2026-10): what the analytics agent's SQL can see. Guards three things —
/// the catalog and the migrations never drift apart, no sensitive column slips into a view, and
/// the system prompt describes exactly the views that exist.
/// </summary>
public class AgentViewCatalogTests
{
    [Fact]
    public void EveryViewIsPrefixedAndEveryColumnIsDescribedOnce()
    {
        Assert.NotEmpty(AgentViewCatalog.Views);
        Assert.Equal(AgentViewCatalog.Views.Count, AgentViewCatalog.ViewNames.Distinct().Count());
        foreach (var view in AgentViewCatalog.Views)
        {
            Assert.StartsWith(AgentViewCatalog.Prefix, view.Name);
            Assert.False(string.IsNullOrWhiteSpace(view.Meaning), $"{view.Name} has no description.");
            Assert.Equal(view.Columns.Count, view.Columns.Select(c => c.Name).Distinct().Count());
            foreach (var c in view.Columns)
            {
                Assert.Matches("^[a-z][a-z0-9_]*$", c.Name);
                Assert.False(string.IsNullOrWhiteSpace(c.Meaning), $"{view.Name}.{c.Name} has no description.");
                Assert.False(string.IsNullOrWhiteSpace(c.Type), $"{view.Name}.{c.Name} has no type.");
            }
        }
    }

    /// <summary>
    /// The columns the views exist to keep away from the agent. Checked against the SOURCE column
    /// each view column reads, so renaming an alias cannot smuggle one in.
    /// </summary>
    private static readonly string[] ForbiddenSourceColumns =
    {
        "PasswordHash", "PasswordSalt", "RefreshToken", "PreviousRefreshToken", "EmailVerificationToken",
        "LastEmailVerificationTokenHash", "PasswordResetToken", "EmailChangeToken", "PendingEmail",
        "LoginOtpCode", "TwoFactorPinHash", "TokenVersion", "StripeCustomerId", "DefaultPaymentMethodId",
        "SavedCardBrand", "SavedCardLast4", "PrimaryPaymentMethodId", "BackupPaymentMethodId",
        "PaymentIntentId", "PaymentAccessToken", "PaymentReference", "PaymentNotes", "PaymentDetails",
        "PaymentConsentIpAddress", "GiftCardCode", "PublicToken", "StripeRefundId",
        "Email", "ContactEmail", "NoticeEmail", "Phone", "ContactPhone",
        "ContactFirstName", "ContactLastName", "ServiceAddress", "AptSuite", "ApartmentName", "PrincipalAddress",
        "Location", "EntryMethod", "SpecialInstructions", "CancellationReason", "Notes", "MainNote", "Message",
        "Allergies", "Restrictions", "Nationality", "Age", "DocumentUrl", "PhotoUrl", "ProfilePictureUrl",
        "AcquisitionClickId", "AcquisitionReferrer", "AcquisitionLandingPage", "AuthorName", "Text", "ReplyText",
        "Reason", "FailureReason", "LostReason", "CustomerNote", "InternalNote", "RecipientEmailsJson",
        "DefaultInvoiceRecipientsJson", "ExternalAuthId", "ViewablePages"
    };

    [Fact]
    public void NoViewReadsASensitiveColumn()
    {
        var leaks = new List<string>();
        foreach (var view in AgentViewCatalog.Views)
        foreach (var column in view.Columns)
        {
            var sources = Regex.Matches(column.Sql, @"`(\w+)`").Select(m => m.Groups[1].Value);
            leaks.AddRange(sources.Where(s => ForbiddenSourceColumns.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Select(s => $"{view.Name}.{column.Name} reads {s}"));
        }
        Assert.True(leaks.Count == 0, string.Join("; ", leaks));
    }

    [Fact]
    public void EveryViewInTheCatalogIsCreatedVerbatimByAMigration()
    {
        // The migration pastes the generated SQL so it stays frozen. Changing a view here without a
        // new migration would make the prompt describe columns the database does not have.
        var migrationsDir = Path.Combine(BackendDir(), "Migrations");
        var all = string.Join("\n", Directory.GetFiles(migrationsDir, "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var missing = AgentViewCatalog.Views
            .Where(v => !all.Contains(AgentViewCatalog.CreateViewSql(v), StringComparison.Ordinal))
            .Select(v => v.Name)
            .ToList();
        Assert.True(missing.Count == 0,
            "These views differ from what the migrations create - add a migration that pastes the new AgentViewCatalog.CreateViewSql output: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void AgentOrdersExposesTheArrangedCleaningKindAsTheDropdownLabel()
    {
        // Order.CustomServiceDisplayName stores the label picked in the order's "Change service
        // type" dropdown; blank or missing reads as NULL ("Not set (Arranged)").
        var orders = AgentViewCatalog.Views.Single(v => v.Name == "agent_orders");
        var column = Assert.Single(orders.Columns, c => c.Name == "arranged_cleaning_kind");
        Assert.Equal("NULLIF(TRIM(o.`CustomServiceDisplayName`), '')", column.Sql);
        Assert.Equal("text", column.Type);
        Assert.Equal(
            "Cleaning kind chosen on the order (Regular, Deep, Move In/Out, Office, Custom, Heavy, Filthy, Post Construction); set mainly on Pre-arranged orders, NULL = not set.",
            column.Meaning);
        Assert.Contains("- arranged_cleaning_kind (text): Cleaning kind chosen on the order", AgentViewCatalog.PromptSchema());
    }

    [Fact]
    public void ThePromptDescribesEveryViewAndKeepsTheBusinessDefinitionsSection()
    {
        var prompt = AdminAgentSystemPrompt.Build();
        foreach (var view in AgentViewCatalog.Views)
            Assert.Contains("### " + view.Name, prompt);
        Assert.Contains("# BUSINESS DEFINITIONS (editable)", prompt);
        foreach (var term in new[] { "Company Revenue (Statistics)", "Statistics Net Income", "Admins-tab net", "Booking admin", "Cleaner pay", "Recurring", "Sales tax" })
            Assert.Contains(term, prompt);
        Assert.Contains("8.875%", prompt);
        Assert.Contains(AdminAgentSystemPrompt.CompanyName, prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void GrantsAreSelectOnTheViewsOnly()
    {
        var grants = AgentViewCatalog.GrantSql("DreamCleaningDB", "dc_agent_ro", "localhost").ToList();
        Assert.Equal(AgentViewCatalog.Views.Count, grants.Count);
        Assert.All(grants, g => Assert.Matches(@"^GRANT SELECT ON `DreamCleaningDB`\.`agent_\w+` TO 'dc_agent_ro'@'localhost';$", g));
    }

    [Theory]
    [InlineData("2026-10-12", "week", "2026-10-12 to 2026-10-18")] // a Monday
    [InlineData("2026-10-18", "week", "2026-10-12 to 2026-10-18")] // the Sunday of the same week
    [InlineData("2026-10-16", "month", "2026-10")]
    [InlineData("2026-10-16", "day", "2026-10-16")]
    public void PeriodsBucketMondayToSunday(string date, string period, string expected)
    {
        Assert.Equal(expected, AdminAgentTools.PeriodKey(DateTime.Parse(date), period));
    }

    [Fact]
    public void CostIsEstimatedOnlyWhenPricesAreConfigured()
    {
        var none = new AdminAgentSettings();
        Assert.Null(none.EstimateCost(1_000_000, 1_000_000, 0, 0));

        var priced = new AdminAgentSettings { PriceInputPerMTok = 2m, PriceOutputPerMTok = 10m, PriceCacheReadPerMTok = 0.2m };
        // Cache writes without their own price fall back to the input price.
        Assert.Equal(2m + 10m + 2m + 0.2m, priced.EstimateCost(1_000_000, 1_000_000, 1_000_000, 1_000_000));
    }

    [Fact]
    public void TheAgentKeyFallsBackToTheChatKey_AndDefaultsHold()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "chat-key", ["AdminAgent:Effort"] = "bogus" })
            .Build();
        var s = AdminAgentSettings.From(config);
        Assert.Equal("chat-key", s.ApiKey);
        Assert.Equal(AdminAgentSettings.DefaultModel, s.Model);
        Assert.Equal(12, s.MaxToolIterations);
        Assert.Equal("medium", s.Effort);
    }

    internal static string BackendDir()
    {
        var dir = new DirectoryInfo(SourceTree.TestsProjectDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend", "Migrations")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "DreamCleaningBackend");
    }
}
