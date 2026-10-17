using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// The transaction a catalogue operation opens (2026-10). On its own — an admin clicking Save —
    /// it is a real transaction, committed or rolled back exactly as the controller's own used to
    /// be. Inside a change-set apply or preview, which already holds the connection's transaction,
    /// it JOINS that one: commit and rollback do nothing and the outer owner decides, so a batch of
    /// operations is all-or-nothing and a preview leaves nothing behind.
    /// </summary>
    public sealed class CatalogTransaction : IDisposable, IAsyncDisposable
    {
        private readonly IDbContextTransaction? _own;

        private CatalogTransaction(IDbContextTransaction? own) => _own = own;

        public static async Task<CatalogTransaction> BeginAsync(ApplicationDbContext context) =>
            context.Database.CurrentTransaction != null
                ? new CatalogTransaction(null)
                : new CatalogTransaction(await context.Database.BeginTransactionAsync());

        public Task CommitAsync() => _own?.CommitAsync() ?? Task.CompletedTask;

        public Task RollbackAsync() => _own?.RollbackAsync() ?? Task.CompletedTask;

        public void Dispose() => _own?.Dispose();

        public ValueTask DisposeAsync() => _own?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
