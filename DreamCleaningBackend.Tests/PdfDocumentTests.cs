using System.Text;
using System.Text.RegularExpressions;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Services.Contracts;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Every PDF type the backend generates renders, with the page count its sample data should
/// produce and a real text layer. A QuestPDF upgrade that changes line or page breaks moves a
/// count here before it moves one in a customer's document.
/// </summary>
public class PdfDocumentTests
{
    public static IEnumerable<object[]> Samples() =>
        PdfSampleDocuments.All().Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(Samples))]
    public void EveryPdfTypeRendersWithItsExpectedPageCountAndText(string name)
    {
        var sample = PdfSampleDocuments.All().Single(s => s.Name == name);

        var bytes = sample.Render();

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(sample.ExpectedPages, pdf.NumberOfPages);

        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        Assert.DoesNotContain('\0', text);
        Assert.Contains(Squash(sample.MustContain), Squash(text), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The contract and policy counts above were taken against this canonical text. When the
    /// wording legitimately changes, its version moves too - recount the pages then.
    /// </summary>
    [Fact]
    public void PageCountsWereTakenAgainstTheCurrentCanonicalText()
    {
        Assert.Equal("2.0", CommercialPolicyDocument.Version);
        Assert.Equal(ContractTemplateSeed.TemplateVersion,
            ContractGeneratedDocumentRegressionTests.Dcc49303882Configuration().ContractTemplateVersion);
    }

    private static string Squash(string value) => Regex.Replace(value, @"\s+", string.Empty);
}

/// <summary>
/// QuestPDF's global settings and the bundled fonts. These tests flip a process-wide setting, so
/// they run on their own rather than beside the parallel test classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class QuestPdfGlobalSettingsCollection
{
    public const string Name = "QuestPDF global settings";
}

[Collection(QuestPdfGlobalSettingsCollection.Name)]
public class PdfFontCoverageTests
{
    /// <summary>
    /// The app draws a character no shipped font covers as an empty box rather than failing the
    /// document. This is the other half of that decision: with QuestPDF's check ON, every script
    /// our customers write in renders, in every document type that prints customer text.
    /// </summary>
    [Fact]
    public void EveryScriptCustomersWriteInIsCoveredByTheBundledFonts()
    {
        QuestPdfSetup.Configure();
        var before = QuestPDF.Settings.ThrowOnMissingTextGlyphs;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
        try
        {
            var text = PdfSampleDocuments.Multilingual;
            new DreamCleaningBackend.Services.Commercial.InvoicePdfService().Render(
                PdfSampleDocuments.CommercialInvoice(i => { i.ClientName = text; i.CustomerNote = text; }));
            new DreamCleaningBackend.Services.CustomerInvoicePdfService().Render(
                PdfSampleDocuments.CustomerInvoice(i => { i.BilledToName = text; i.ServiceAddress = text; i.Note = text; }));
            PdfSampleDocuments.Contract(certificate: true, draft: false, s => s.Client.LegalEntityName = text);
        }
        finally
        {
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = before;
        }
    }

    [Fact]
    public void EveryFallbackFamilyIsShippedAndTheChainFitsQuestPdfsLimit()
    {
        Assert.Empty(QuestPdfSetup.MissingFontFamilies());
        Assert.Equal("Lato", QuestPdfSetup.FontFamilies[0]);
        // QuestPDF silently drops families past its native limit - the emoji face once was.
        Assert.InRange(QuestPdfSetup.FontFamilies.Length, 1, QuestPdfSetup.MaxFontFamilies);
        Assert.Equal(QuestPdfSetup.FontFamilies.Length,
            QuestPdfSetup.FontFamilies.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PdfServicesApplyTheAppSettings()
    {
        _ = new DreamCleaningBackend.Services.CustomerInvoicePdfService();

        Assert.Equal(QuestPDF.Infrastructure.LicenseType.Community, QuestPDF.Settings.License);
        Assert.False(QuestPDF.Settings.UseSystemFonts);
        Assert.False(QuestPDF.Settings.ThrowOnMissingTextGlyphs);
        Assert.False(QuestPDF.Settings.ThrowOnMissingFontFamilies);
        Assert.False(QuestPDF.Settings.EnableDetailedLayoutErrors);
        Assert.Equal(QuestPdfSetup.FontDirectory, QuestPDF.Settings.FontDiscoveryPath);
    }
}
