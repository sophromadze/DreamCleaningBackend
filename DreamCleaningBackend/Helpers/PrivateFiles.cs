using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// PRIVATE UPLOADS (2026-10). Cleaner photos and documents, customer cleaning photos and chat
    /// photos used to be served straight off disk by Apache (and by this API's own static-file
    /// mapping) to anyone holding the URL. They are now reachable ONLY through endpoints that check
    /// the caller on every request (PrivateFilesController, ChatController's session image route).
    ///
    /// The database still stores the old "/folder/name.ext" paths, on purpose: that value is also
    /// how every delete/cleanup path finds the file on disk. So nothing is migrated — a stored path
    /// is turned into an endpoint URL at the moment it leaves the API (the methods below), and old
    /// and new rows behave identically.
    /// </summary>
    public static class PrivateFileUrls
    {
        public const string CleanerPhotosFolder = "cleaners/photos";
        public const string CleanerDocumentsFolder = "cleaners/documents";
        public const string CleaningPhotosFolder = "user-cleaning-photos";
        public const string ChatPhotosFolder = "chat-photos";

        /// <summary>Upload names are a GUID and the extension the upload endpoint chose — nothing else.</summary>
        private static readonly Regex ChatFileNameRegex =
            new(@"^[0-9a-fA-F]{32}\.(jpg|png|webp)$", RegexOptions.Compiled);

        /// <summary>
        /// What a stored value actually is. Not every row holds one of our upload paths: some
        /// cleaners' PhotoUrl is the Google profile picture they signed in with
        /// (https://lh3.googleusercontent.com/...), and only a LOCAL path can be served by the
        /// access-checked endpoints — converting the rest produced 404s in production (2026-10).
        /// </summary>
        public enum StoredFileKind { None, Local, ExternalHttps, Unsupported }

        public static StoredFileKind Classify(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return StoredFileKind.None;
            // Checked first: on Linux "/cleaners/x.webp" also parses as an absolute file:// URI.
            if (stored.StartsWith('/') && !stored.StartsWith("//")) return StoredFileKind.Local;
            return Uri.TryCreate(stored, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host)
                ? StoredFileKind.ExternalHttps
                : StoredFileKind.Unsupported; // http://, protocol-relative, junk
        }

        /// <summary>
        /// Local photo → the id-based URL plus a short fingerprint of the stored file name (a replaced
        /// photo gets a new name on disk, so the URL changes and the browser cannot keep showing the
        /// old one from its max-age=300 cache). An external https picture is already public on its
        /// own host and is returned as-is — no ?v=. Anything else (plain http would be mixed content,
        /// junk) is not exposed.
        /// </summary>
        public static string? CleanerPhoto(int cleanerId, string? storedPath, ILogger? logger = null)
            => Classify(storedPath) switch
            {
                StoredFileKind.Local => $"/api/files/cleaners/{cleanerId}/photo?v={Fingerprint(storedPath!)}",
                StoredFileKind.ExternalHttps => storedPath,
                StoredFileKind.Unsupported => Rejected(logger, "cleaner photo", cleanerId, storedPath!),
                _ => null
            };

        /// <summary>
        /// Identity documents are only ever our own uploads. An external URL in this column should be
        /// impossible; if one appears it is NOT handed to the browser (we cannot vouch for where it
        /// points or who else can read it) and a warning is logged instead.
        /// </summary>
        public static string? CleanerDocument(int cleanerId, string? storedPath, ILogger? logger = null)
            => Classify(storedPath) switch
            {
                StoredFileKind.Local => $"/api/files/cleaners/{cleanerId}/document?v={Fingerprint(storedPath!)}",
                StoredFileKind.None => null,
                _ => Rejected(logger, "cleaner document", cleanerId, storedPath!)
            };

        /// <summary>Customer cleaning photos are private uploads too: same rule as documents.</summary>
        public static string? CleaningPhoto(int photoId, string? storedPath, ILogger? logger = null)
            => Classify(storedPath) switch
            {
                StoredFileKind.Local => $"/api/files/cleaning-photos/{photoId}",
                StoredFileKind.None => null,
                _ => Rejected(logger, "customer cleaning photo", photoId, storedPath!)
            };

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Warned = new();

        /// <summary>Logs once per row per process (list pages map the same rows on every load).</summary>
        private static string? Rejected(ILogger? logger, string what, int id, string stored)
        {
            if (logger != null && Warned.TryAdd($"{what}:{id}", 0))
            {
                // Scheme and host only: the rest of a URL can carry tokens.
                var shown = Uri.TryCreate(stored, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
                    ? $"{u.Scheme}://{u.Host}/..." : "(not a local upload path)";
                logger.LogWarning("Stored {What} for id {Id} is not a local upload ({Value}); not exposed to the browser", what, id, shown);
            }
            return null;
        }

        /// <summary>The visitor's own copy: valid only together with the session it was sent in.</summary>
        public static string? ChatImageForSession(Guid sessionId, string? storedPath)
            => ChatFileName(storedPath) is { } name ? $"/api/chat/session/{sessionId}/images/{name}" : null;

        /// <summary>Staff copy (admin chat viewer) — cookie-authenticated.</summary>
        public static string? ChatImageForStaff(string? storedPath)
            => ChatFileName(storedPath) is { } name ? $"/api/files/chat/{name}" : null;

        /// <summary>"/chat-photos/{guid}.jpg" → "{guid}.jpg"; null for anything else.</summary>
        public static string? ChatFileName(string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath)) return null;
            const string prefix = "/" + ChatPhotosFolder + "/";
            if (!storedPath.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var name = storedPath.Substring(prefix.Length);
            return IsChatFileName(name) ? name : null;
        }

        public static bool IsChatFileName(string? fileName)
            => fileName != null && ChatFileNameRegex.IsMatch(fileName);

        public static string StoredChatPath(string fileName) => "/" + ChatPhotosFolder + "/" + fileName;

        private static string Fingerprint(string storedPath)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(storedPath));
            return Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
        }

        /// <summary>Random, unguessable name for a NEW private upload. Existing files keep theirs.</summary>
        public static string NewFileName(string extension) => $"{Guid.NewGuid():N}{extension}";
    }

    /// <summary>
    /// The API's own static-file mapping of the uploads root. It used to serve the WHOLE root at
    /// "/", private folders included, to anyone who could reach this port; now only the public
    /// folders are mapped, each at its own path. Production serves these through Apache aliases,
    /// so this matters for the dev proxy and as defence in depth.
    /// </summary>
    public static class PublicUploadStaticFiles
    {
        public static readonly string[] Folders = { "images", "blog-images", "before-after" };

        /// <summary>
        /// {root}/public is served at /uploads (2026-10): the home of NEW public upload folders
        /// (gift-cards first). Only what sits under public/ is ever web-served, so adding a
        /// public upload folder later can never expose a private one.
        /// </summary>
        public const string PublicFolder = "public";
        public const string PublicUrlPrefix = "/uploads";

        public static void Use(IApplicationBuilder app, string uploadRoot)
        {
            var publicPath = Path.Combine(uploadRoot, PublicFolder);
            Directory.CreateDirectory(publicPath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(publicPath),
                RequestPath = PublicUrlPrefix
            });

            foreach (var folder in Folders)
            {
                var folderPath = Path.Combine(uploadRoot, folder);
                Directory.CreateDirectory(folderPath);
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(folderPath),
                    RequestPath = "/" + folder
                });
            }
        }
    }

    /// <summary>Maps a stored "/folder/name.ext" path to a file on disk — or to nothing.</summary>
    public static class PrivateFileStore
    {
        /// <summary>
        /// The full path of an existing file that lives inside {uploadRoot}/{folder}, or null. The
        /// stored path must name that folder, and the resolved path must still be inside it after
        /// normalisation, so neither "../" in a corrupted row nor a path from another folder can
        /// escape. Callers only ever pass a path read from the database, never one from the request.
        /// </summary>
        public static string? Resolve(string? uploadRoot, string? storedPath, string folder)
        {
            if (string.IsNullOrWhiteSpace(uploadRoot) || string.IsNullOrWhiteSpace(storedPath))
                return null;

            var prefix = "/" + folder + "/";
            if (!storedPath.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            try
            {
                var folderRoot = Path.GetFullPath(Path.Combine(uploadRoot, folder.Replace('/', Path.DirectorySeparatorChar)));
                var relative = storedPath.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(folderRoot, relative));
                var inside = full.StartsWith(folderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                return inside && File.Exists(full) ? full : null;
            }
            catch (Exception)
            {
                return null; // malformed path characters etc. — treat as "no such file"
            }
        }
    }

    /// <summary>How every private file leaves the API: same headers, inline, safe name.</summary>
    public static class PrivateFileResponse
    {
        public const string NoStore = "private, no-store";
        public const string ShortPrivateCache = "private, max-age=300";

        /// <param name="downloadName">Built by the caller from ids — never a name from disk or the request.</param>
        public static IActionResult Serve(ControllerBase controller, string fullPath, string downloadName, string cacheControl)
        {
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".webp" => "image/webp",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };

            var headers = controller.Response.Headers;
            headers[HeaderNames.CacheControl] = cacheControl;
            headers["X-Content-Type-Options"] = "nosniff";
            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(downloadName + extension);
            headers[HeaderNames.ContentDisposition] = disposition.ToString();

            return controller.PhysicalFile(fullPath, contentType);
        }
    }

    /// <summary>
    /// Short-lived signed links for the one place a private file is referenced OUTSIDE the
    /// signed-in site: the chat escalation transcript, which is posted to the team's Telegram topic
    /// and emailed to the company inbox. Neither carries the site cookie (Telegram is another app;
    /// a link clicked in a mail client is a cross-site navigation, and the auth cookie is
    /// SameSite=Strict), so those two get an expiring HMAC link instead.
    ///
    /// The key is derived from the existing JWT signing key (AppSettings:Token) with a fixed label,
    /// so no new configuration is needed and a link can never double as a token. No key → no link.
    /// </summary>
    public static class PrivateFileLinkSigner
    {
        public static readonly TimeSpan ChatTranscriptLifetime = TimeSpan.FromHours(72);
        private const string Label = "private-file-link:v1";

        public static string? SignedChatLink(string? signingSecret, string siteUrl, string? storedPath, DateTime nowUtc)
        {
            var name = PrivateFileUrls.ChatFileName(storedPath);
            if (name == null || string.IsNullOrWhiteSpace(signingSecret)) return null;

            var expires = new DateTimeOffset(nowUtc.Add(ChatTranscriptLifetime)).ToUnixTimeSeconds();
            var sig = Signature(signingSecret, "chat", name, expires);
            return $"{siteUrl.TrimEnd('/')}/api/files/chat/{name}?exp={expires}&sig={sig}";
        }

        public static bool IsValidChatLink(string? signingSecret, string fileName, long? expires, string? sig, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(signingSecret) || expires == null || string.IsNullOrEmpty(sig))
                return false;
            if (DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, expires.Value)).UtcDateTime <= nowUtc)
                return false;

            var expected = Encoding.ASCII.GetBytes(Signature(signingSecret, "chat", fileName, expires.Value));
            var given = Encoding.ASCII.GetBytes(sig);
            return CryptographicOperations.FixedTimeEquals(expected, given);
        }

        private static string Signature(string secret, string kind, string fileName, long expires)
        {
            using var keyHmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var key = keyHmac.ComputeHash(Encoding.UTF8.GetBytes(Label));
            using var hmac = new HMACSHA256(key);
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{kind}\n{fileName}\n{expires}"));
            return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
