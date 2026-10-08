using DreamCleaningBackend.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// EVERY UPLOAD GOES THROUGH THE SAME THREE CALLS: load, shrink to fit (ResizeMode.Max), save
    /// as WebP (2026-10, ImageSharp 3.1 -> 4.1).
    ///
    /// ImageSharp 4 reworked colour conversion, decoding of ICC-tagged JPEGs and the encoders.
    /// These tests run that pipeline — the real ResponsiveImageVariants helper and the same
    /// load/resize/encode calls the upload paths make — and pin what the site depends on: the
    /// output is WebP, the aspect ratio is kept, nothing is upscaled, and a photo's colours come
    /// through unchanged.
    /// </summary>
    public sealed class ImagePipelineTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-image-pipeline-" + Guid.NewGuid().ToString("N"));

        public ImagePipelineTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
        }

        [Fact]
        public async Task ResponsiveVariantsAreWebp_KeepTheAspectRatio_AndNeverUpscale()
        {
            var original = Path.Combine(_dir, "before-1.webp");
            using var source = Solid(1350, 900, new Rgb24(200, 100, 50));
            await source.SaveAsync(original, new WebpEncoder { Quality = 82 }, TestContext.Current.CancellationToken);

            var written = await ResponsiveImageVariants.WriteMissingAsync(source, original, quality: 80);

            Assert.Equal(2, written);
            foreach (var (width, height) in new[] { (400, 267), (800, 533) })
            {
                var path = ResponsiveImageVariants.VariantPath(original, width);
                var info = await Image.IdentifyAsync(path, TestContext.Current.CancellationToken);
                Assert.Equal(width, info.Width);
                Assert.Equal(height, info.Height);
                Assert.IsType<WebpFormat>(info.Metadata.DecodedImageFormat);
            }

            // A second call writes nothing: the variants are already on disk.
            Assert.Equal(0, await ResponsiveImageVariants.WriteMissingAsync(source, original, quality: 80));

            var srcset = await ResponsiveImageVariants.BuildSrcsetAsync("/before-after/before-1.webp", original);
            Assert.Equal(
                "/before-after/before-1-400w.webp 400w, /before-after/before-1-800w.webp 800w, /before-after/before-1.webp 1350w",
                srcset);
        }

        [Fact]
        public async Task ASmallOriginalGetsNoVariants()
        {
            var original = Path.Combine(_dir, "small.webp");
            using var source = Solid(380, 300, new Rgb24(10, 20, 30));
            await source.SaveAsync(original, new WebpEncoder(), TestContext.Current.CancellationToken);

            Assert.Equal(0, await ResponsiveImageVariants.WriteMissingAsync(source, original, quality: 80));
            Assert.Null(await ResponsiveImageVariants.BuildSrcsetAsync("/x/small.webp", original));
        }

        [Fact]
        public async Task AJpegUploadIsShrunkToFitAndKeepsItsColours()
        {
            // A phone photo arrives as JPEG; the upload paths shrink it into a box and store WebP.
            using var upload = new MemoryStream();
            using (var photo = Solid(4032, 3024, new Rgb24(46, 139, 87)))
                await photo.SaveAsJpegAsync(upload, new JpegEncoder { Quality = 90 }, TestContext.Current.CancellationToken);
            upload.Position = 0;

            var stored = Path.Combine(_dir, "photo.webp");
            using (var image = await Image.LoadAsync(upload, TestContext.Current.CancellationToken))
            {
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1920, 1920), Mode = ResizeMode.Max }));
                await image.SaveAsync(stored, new WebpEncoder { Quality = 82, Method = WebpEncodingMethod.BestQuality },
                    TestContext.Current.CancellationToken);
            }

            using var result = await Image.LoadAsync<Rgb24>(stored, TestContext.Current.CancellationToken);
            Assert.Equal(1920, result.Width);
            Assert.Equal(1440, result.Height);

            var centre = result[result.Width / 2, result.Height / 2];
            Assert.InRange(centre.R, 46 - 6, 46 + 6);
            Assert.InRange(centre.G, 139 - 6, 139 + 6);
            Assert.InRange(centre.B, 87 - 6, 87 + 6);
        }

        [Fact]
        public async Task AnEmptyOrNonImageUploadIsRefusedWithAnImageFormatError()
        {
            // ImageSharp 4 throws for empty input instead of returning; both cases must keep failing
            // as an ImageFormatException, which the upload paths' error handling already covers.
            await Assert.ThrowsAnyAsync<ImageFormatException>(
                () => Image.LoadAsync(new MemoryStream(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<ImageFormatException>(
                () => Image.LoadAsync(new MemoryStream("not an image"u8.ToArray()), TestContext.Current.CancellationToken));
        }

        private static Image<Rgb24> Solid(int width, int height, Rgb24 colour) => new(width, height, colour);
    }
}
