using Microsoft.Extensions.Configuration;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>
    /// Where generated contract documents live on disk.
    ///
    /// Deliberately NOT under <c>FileUpload:Path</c>: that whole directory is mounted at the URL
    /// root by a PhysicalFileProvider in Program.cs, so anything placed there - including a
    /// subfolder - is publicly fetchable by anyone who guesses the filename. Executed contracts
    /// carry legal identities, addresses and signatures, so they sit outside the served tree and
    /// are only reachable through the authorized or token-scoped download endpoints.
    ///
    /// Override with <c>Contracts:StoragePath</c>; defaults to <c>App_Data/contracts</c> under the
    /// content root.
    /// </summary>
    public class ContractStorage
    {
        private readonly string _root;

        public ContractStorage(IConfiguration configuration, IWebHostEnvironment environment)
        {
            var configured = configuration["Contracts:StoragePath"];
            _root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(environment.ContentRootPath, "App_Data", "contracts")
                : Path.GetFullPath(configured);
            Directory.CreateDirectory(_root);
        }

        /// <summary>Absolute path for a path stored on a <c>ContractFile</c> row.</summary>
        public string Resolve(string relativePath) =>
            Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// Writes bytes under <c>{contractNumber}/{fileName}</c> and returns the RELATIVE path to
        /// store on the row. Relative so a server move or a changed storage root does not
        /// invalidate every historical file reference.
        /// </summary>
        public async Task<string> SaveAsync(string contractNumber, string fileName, byte[] bytes)
        {
            var safeFolder = SafeSegment(contractNumber);
            var safeName = SafeSegment(fileName);
            var folder = Path.Combine(_root, safeFolder);
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, safeName), bytes);
            return $"{safeFolder}/{safeName}";
        }

        public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

        public Task<byte[]> ReadAsync(string relativePath) => File.ReadAllBytesAsync(Resolve(relativePath));

        /// <summary>Strips anything that could escape the storage root or break a filesystem.</summary>
        private static string SafeSegment(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            cleaned = cleaned.Replace("..", "_");
            return string.IsNullOrWhiteSpace(cleaned) ? "unnamed" : cleaned;
        }
    }
}
