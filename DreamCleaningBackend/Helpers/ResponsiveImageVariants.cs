using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Smaller WebP copies of an uploaded image, saved next to the original with a width suffix:
    ///   before-20260704142045385.webp  ->  before-20260704142045385-400w.webp, ...-800w.webp
    /// The browser picks one through <c>srcset</c>, so a 196px-wide gallery card no longer downloads
    /// a 1350px original. Rules: aspect ratio is always kept (only the width is set), nothing is
    /// cropped, and nothing is upscaled — a width at or above the original's is simply not written,
    /// and the original itself is the largest <c>srcset</c> candidate.
    /// </summary>
    public static class ResponsiveImageVariants
    {
        public static readonly int[] Widths = { 400, 800 };

        /// <summary>"/before-after/x.webp" + 400 → "/before-after/x-400w.webp". Works for disk paths too.</summary>
        public static string VariantPath(string path, int width)
        {
            var extension = Path.GetExtension(path);
            return $"{path[..^extension.Length]}-{width}w{extension}";
        }

        /// <summary>Widths this original should have variants for (narrower than the original only).</summary>
        public static IEnumerable<int> WidthsFor(int originalWidth) => Widths.Where(w => w < originalWidth);

        /// <summary>
        /// Writes every missing variant of <paramref name="source"/>, whose original lives at
        /// <paramref name="originalFullPath"/>. Existing variants are left alone, so it is safe to
        /// call again. Returns how many files were written.
        /// </summary>
        public static async Task<int> WriteMissingAsync(Image source, string originalFullPath, int quality)
        {
            var written = 0;
            foreach (var width in WidthsFor(source.Width))
            {
                var variantPath = VariantPath(originalFullPath, width);
                if (File.Exists(variantPath)) continue;

                using var resized = source.Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(width, 0),
                    Mode = ResizeMode.Max
                }));
                // Write to a temp name and move into place, so a request arriving mid-write never
                // sees (or caches) a half-written file.
                var tempPath = variantPath + ".tmp";
                await resized.SaveAsync(tempPath, new WebpEncoder
                {
                    Quality = quality,
                    Method = WebpEncodingMethod.BestQuality
                });
                File.Move(tempPath, variantPath, overwrite: true);
                written++;
            }
            return written;
        }

        /// <summary>
        /// <c>srcset</c> for the original at <paramref name="publicUrl"/>, listing only variants that
        /// exist on disk plus the original at its real width. Null when no variant exists yet (or the
        /// original can't be read) — the page then uses the plain URL exactly as before.
        /// </summary>
        public static async Task<string?> BuildSrcsetAsync(string publicUrl, string originalFullPath)
        {
            if (!File.Exists(originalFullPath)) return null;

            ImageInfo info;
            try
            {
                info = await Image.IdentifyAsync(originalFullPath);
            }
            catch
            {
                return null;
            }

            var candidates = WidthsFor(info.Width)
                .Where(w => File.Exists(VariantPath(originalFullPath, w)))
                .Select(w => $"{VariantPath(publicUrl, w)} {w}w")
                .ToList();
            if (candidates.Count == 0) return null;

            candidates.Add($"{publicUrl} {info.Width}w");
            return string.Join(", ", candidates);
        }

        /// <summary>Deletes every variant of the original at <paramref name="originalFullPath"/>.</summary>
        public static void DeleteAll(string originalFullPath)
        {
            foreach (var width in Widths)
            {
                try
                {
                    var variantPath = VariantPath(originalFullPath, width);
                    if (File.Exists(variantPath)) File.Delete(variantPath);
                }
                catch
                {
                    // Same policy as the original's own deletion: never fail the request over disk cleanup.
                }
            }
        }
    }
}
