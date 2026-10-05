using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using DreamCleaningBackend.Services.Contracts;
using UglyToad.PdfPig;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// REGRESSION: PDFs generated at the same moment could come out with an empty text layer.
///
/// QuestPDF 2024.12.3 (Windows x64) occasionally wrote a font ToUnicode map of all U+0000 when two
/// documents were generated concurrently: the page looked right, but its text could not be
/// selected, copied, searched or read aloud. It surfaced as a flaky
/// <c>ContractGeneratedDocumentRegressionTests</c> failure whose extracted PDF text was nothing but
/// NUL characters, whenever a parallel test class happened to be generating a PDF at the same time.
/// <c>Helpers/PdfGeneration.cs</c> serializes generation; these tests hold it in place.
/// </summary>
public class PdfGenerationTests
{
    public PdfGenerationTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    /// <summary>
    /// The deterministic half: every generator goes through the serialized helper. A new PDF
    /// service calling QuestPDF's GeneratePdf() directly would reopen the race.
    /// </summary>
    [Fact]
    public void NoServiceCallsQuestPdfGenerateDirectly()
    {
        var backend = Path.Combine(Directory.GetParent(SourceTree.TestsProjectDir)!.FullName, "DreamCleaningBackend");
        var helper = Path.Combine(backend, "Helpers", "PdfGeneration.cs");
        Assert.True(File.Exists(helper), $"{helper} was not found.");

        var offenders = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !string.Equals(f, helper, StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.GeneratePdf\s*\("))
            .Select(f => Path.GetRelativePath(backend, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Generate PDFs with GeneratePdfExclusive() (Helpers/PdfGeneration.cs), not GeneratePdf(): "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The behavioural half: contract PDFs generated from many threads at once all keep a readable
    /// text layer. Without the lock roughly 2–3% came out broken, so 64 documents catch a
    /// regression in most runs; with it, none ever should.
    /// </summary>
    [Fact]
    public void ContractPdfsGeneratedConcurrently_AllKeepTheirText()
    {
        var snapshot = ContractGeneratedDocumentRegressionTests.Dcc49303882Configuration();
        var rendered = ContractRenderer.Render(snapshot);
        var block = ContractGeneratedDocumentRegressionTests.Block();

        var documents = new ConcurrentBag<byte[]>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
            documents.Add(new ContractPdfService().GenerateDocument(rendered, snapshot, block, null, true)));

        var unreadable = documents.Count(bytes =>
        {
            using var pdf = PdfDocument.Open(bytes);
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages()) text.Append(page.Text);
            // Whitespace-free: PdfPig places spaces wherever the layout suggests.
            var squashed = Regex.Replace(text.ToString(), @"\s+", string.Empty);
            return !squashed.Contains("masterserviceagreement", StringComparison.OrdinalIgnoreCase)
                   || squashed.Contains('\0');
        });

        Assert.Equal(0, unreadable);
    }
}
