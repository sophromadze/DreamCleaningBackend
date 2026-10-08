using System.Collections.Concurrent;
using System.Text;
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
///
/// From 2026-10-04 to the QuestPDF upgrade, generation was serialized behind a process-wide lock.
/// QuestPDF fixed the race upstream in 2026.7.2, and the lock was REMOVED with the 2026.9.1
/// upgrade after a lockless stress run: 2024.12.3 broke 4 of 800 documents generated 16-wide,
/// 2026.9.1 broke 0 of 9,000 generated 16- and 32-wide. These tests keep it that way.
/// </summary>
public class PdfGenerationTests
{
    /// <summary>
    /// Every generator, many threads at once: each document's text layer must be exactly what the
    /// same document produces on its own. Without the upstream fix roughly 0.5% came out broken,
    /// so 128 documents catch a regression in most runs; with it, none ever should.
    /// </summary>
    [Fact]
    public void PdfsGeneratedConcurrently_AllKeepTheirText()
    {
        var samples = PdfSampleDocuments.All();
        var reference = samples.ToDictionary(s => s.Name, s => Text(s.Render()));

        var broken = new ConcurrentBag<string>();
        Parallel.For(0, 128, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var sample = samples[i % samples.Count];
            if (Text(sample.Render()) != reference[sample.Name]) broken.Add(sample.Name);
        });

        Assert.True(broken.IsEmpty, "Text layer differs from a sequential render: " + string.Join(", ", broken));
        Assert.All(reference.Values, text => Assert.DoesNotContain('\0', text));
    }

    /// <summary>
    /// The lock was removed BECAUSE of the upstream fix. Going back below 2026.7.2 would silently
    /// bring the unsearchable-PDF race back, so a downgrade has to fail here first.
    /// </summary>
    [Fact]
    public void QuestPdfIsAtLeastTheReleaseThatFixedTheConcurrencyRace()
    {
        var version = typeof(QuestPDF.Fluent.Document).Assembly.GetName().Version!;
        Assert.True(version >= new Version(2026, 7, 2),
            $"QuestPDF {version} has the concurrent-generation text-layer race (fixed in 2026.7.2); "
            + "PDF generation is no longer serialized, so do not downgrade below it.");
    }

    private static string Text(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages()) text.Append(page.Text);
        return text.ToString();
    }
}
