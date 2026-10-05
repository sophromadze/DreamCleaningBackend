namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// GIFT CARD BACKGROUND (2026-10). One place that answers "which background does the gift card
    /// actually use right now" for the /gift-cards page, the admin preview and the gift card email.
    ///
    /// Uploads used to be written into {uploads}/images — the folder Apache aliased over the
    /// site's own /images, which is why every code image had to be copied there by hand and why a
    /// deploy could silently lose an uploaded background (the configured file went missing and
    /// all three surfaces showed nothing). Uploads now live in their own folder that deploys never
    /// touch: {uploads}/public/gift-cards, served at /uploads/gift-cards/.
    ///
    /// The configured path is honoured ONLY when it names an existing file in that folder.
    /// Anything else — empty, a legacy /images/... value, or a file that is gone — resolves to the
    /// built-in default, so no surface ever shows a broken image. Nothing is migrated: the stored
    /// value stays as it is until an admin uploads a new background.
    /// </summary>
    public static class GiftCardBackground
    {
        /// <summary>Disk folder under FileUpload:Path. Everything under "public" is web-served.</summary>
        public const string UploadFolder = "public/gift-cards";
        public const string UrlPrefix = "/uploads/gift-cards/";

        /// <summary>Site default, shipped in the frontend build (public/images).</summary>
        public const string DefaultUrl = "/images/gift-card-bg-20250710163148.webp";

        /// <summary>The same picture in the API's Assets folder, for the email (which embeds bytes).</summary>
        public const string DefaultAssetFileName = "gift-card-default.webp";

        public sealed record Resolved(string Url, bool IsDefault, bool ConfiguredImageMissing);

        public static Resolved Resolve(string? uploadRoot, string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return new Resolved(DefaultUrl, IsDefault: true, ConfiguredImageMissing: false);

            return DiskPath(uploadRoot, storedPath) != null
                ? new Resolved(storedPath, IsDefault: false, ConfiguredImageMissing: false)
                : new Resolved(DefaultUrl, IsDefault: true, ConfiguredImageMissing: true);
        }

        /// <summary>The uploaded file for a stored "/uploads/gift-cards/x.webp" path, or null.</summary>
        public static string? DiskPath(string? uploadRoot, string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath) || !storedPath.StartsWith(UrlPrefix, StringComparison.Ordinal))
                return null;
            // PrivateFileStore.Resolve expects "/{folder}/name": re-root the URL onto the disk folder.
            var asStored = "/" + UploadFolder + "/" + storedPath.Substring(UrlPrefix.Length);
            return PrivateFileStore.Resolve(uploadRoot, asStored, UploadFolder);
        }

        /// <summary>The file the email embeds: the uploaded background, else the bundled default.</summary>
        public static string? EmailImagePath(string? uploadRoot, string? storedPath)
        {
            var uploaded = DiskPath(uploadRoot, storedPath);
            if (uploaded != null) return uploaded;
            var fallback = Path.Combine(AppContext.BaseDirectory, "Assets", DefaultAssetFileName);
            return File.Exists(fallback) ? fallback : null;
        }

        public static string NewFileName() => $"gift-card-bg-{Guid.NewGuid():N}.webp";
        public static string UrlFor(string fileName) => UrlPrefix + fileName;
    }
}
