using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The ONE way this app turns a QuestPDF document into bytes: one document at a time.
    ///
    /// WHY (2026-10). QuestPDF 2024.12.3 on Windows x64 has a rare concurrency bug: when two
    /// documents are generated at the same moment, one of them can come out with every entry of
    /// its font's ToUnicode map set to U+0000. The page looks perfect — the content streams and
    /// embedded fonts are byte-identical to a good copy — but selecting, copying or searching the
    /// text yields nothing, and screen readers read nothing. Measured: 3–5 of 160 contract PDFs
    /// generated 16-wide were broken; 0 of 160 generated one at a time. In production that means
    /// a contract, invoice or policy PDF produced while another one was being produced (two
    /// downloads, a download during an invoice email) could be unsearchable for good, and an
    /// executed contract is stored as generated.
    ///
    /// Fixed upstream in QuestPDF 2026.7.2 ("Fixed a rare Windows x64 concurrency issue where
    /// generated PDF documents appeared correct in viewers, but selected and copied text was
    /// corrupted"). Until the package is upgraded, generation is serialized here — QuestPDF itself
    /// notes sequential generation is often as fast as parallel. The lock is safe to keep after
    /// the upgrade; it is just no longer required.
    ///
    /// Never call <c>GeneratePdf()</c> directly — <c>PdfGenerationTests</c> fails on a new caller.
    /// </summary>
    public static class PdfGeneration
    {
        private static readonly object GenerationLock = new();

        public static byte[] GeneratePdfExclusive(this IDocument document)
        {
            lock (GenerationLock)
            {
                return document.GeneratePdf();
            }
        }
    }
}
