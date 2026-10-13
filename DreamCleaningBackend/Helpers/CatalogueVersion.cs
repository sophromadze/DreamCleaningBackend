using System.Runtime.CompilerServices;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// A stamp that changes whenever anything the public booking catalogue is built from changes
    /// (2026-10). The frontend's Node server holds the catalogue, plans, blocked time slots and
    /// public offers in memory for every server render (catalogue-cache.ts, 5-minute TTL); it polls
    /// <c>GET api/booking/catalogue-version</c> every 2 seconds and refreshes the moment the stamp
    /// moves, so an admin's price change reaches the site in seconds rather than minutes.
    ///
    /// Purely in memory - reading it never touches the database. The boot id makes a backend
    /// restart (or a deploy that changed data through a migration) read as a change too. One
    /// process serves the API, so one counter is the whole truth; if that ever changes, the
    /// Node side still falls back to its TTL.
    /// </summary>
    public sealed class CatalogueVersion
    {
        private readonly string _bootId = Guid.NewGuid().ToString("N")[..8];
        private long _counter;

        public string Current => $"{_bootId}-{Interlocked.Read(ref _counter)}";

        public void Bump() => Interlocked.Increment(ref _counter);
    }

    /// <summary>
    /// Bumps <see cref="CatalogueVersion"/> after a successful SaveChanges that added, changed or
    /// deleted a catalogue row. Every catalogue writer goes through SaveChanges (the admin catalogue
    /// and discounts endpoints, special offers, blocked time slots, the pricing import); there are
    /// no bulk ExecuteUpdate/raw-SQL writes to these tables - keep it that way, or bump by hand.
    /// The bump happens AFTER the save, so a refresh it triggers can only ever read the new rows.
    /// </summary>
    public sealed class CatalogueChangeInterceptor : SaveChangesInterceptor
    {
        private static readonly HashSet<Type> CatalogueTypes = new()
        {
            typeof(ServiceType), typeof(Service), typeof(ServiceThreshold), typeof(ServiceRateTier),
            typeof(ExtraService), typeof(Subscription), typeof(SpecialOffer), typeof(BlockedTimeSlot)
        };

        private readonly CatalogueVersion _version;
        private readonly ConditionalWeakTable<DbContext, object> _pending = new();

        public CatalogueChangeInterceptor(CatalogueVersion version) => _version = version;

        /// <summary>True when the tracked changes touch the catalogue (pure, for tests).</summary>
        public static bool TouchesCatalogue(DbContext context) =>
            context.ChangeTracker.Entries().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                CatalogueTypes.Contains(e.Metadata.ClrType));

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Remember(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Remember(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            BumpIfPending(eventData.Context);
            return base.SavedChanges(eventData, result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            BumpIfPending(eventData.Context);
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData)
        {
            if (eventData.Context != null) _pending.Remove(eventData.Context);
            base.SaveChangesFailed(eventData);
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context != null) _pending.Remove(eventData.Context);
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }

        private void Remember(DbContext? context)
        {
            if (context != null && TouchesCatalogue(context)) _pending.AddOrUpdate(context, true);
        }

        private void BumpIfPending(DbContext? context)
        {
            if (context != null && _pending.Remove(context)) _version.Bump();
        }
    }
}
