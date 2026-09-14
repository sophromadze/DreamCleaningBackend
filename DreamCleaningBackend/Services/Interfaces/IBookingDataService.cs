using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// A prepare-payment attempt: the booking data plus the Stripe intent that was created for
    /// it. Held so a repeated prepare for the SAME logical attempt reuses the existing intent
    /// instead of creating a second one (duplicate-booking fix, 2026-09).
    /// </summary>
    public class PreparedBookingSession
    {
        public string SessionId { get; set; } = "";
        public int UserId { get; set; }
        public CreateBookingDto BookingData { get; set; }

        /// <summary>Hash of the normalised booking data AND the recomputed total. The total is
        /// part of it deliberately: if a gift card balance, loyalty percentage or promo changed
        /// between two attempts, the existing intent is for the wrong amount and must not be
        /// reused.</summary>
        public string Fingerprint { get; set; } = "";

        public string? PaymentIntentId { get; set; }
        public string? PaymentClientSecret { get; set; }
        public decimal Total { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True only for prepare-payment attempts, which are the ONLY entries the TTL sweep may
        /// remove. The same dictionary also holds `booking_{orderId}_{userId}` entries written by
        /// CreateBooking and create-for-user, and those are read back when the customer pays —
        /// possibly days later, from an emailed payment link. They have no TTL: they are removed
        /// on a successful confirm (RemoveBookingData) or at process restart, and expiring them
        /// silently drops the customer's bubble points, reward credits, uploaded photos and
        /// save-card choice from the order. Set by StorePreparedSession, never by StoreBookingData.
        /// </summary>
        public bool IsPrepareSession { get; set; }
    }

    public interface IBookingDataService
    {
        void StoreBookingData(string sessionId, CreateBookingDto bookingData);
        CreateBookingDto GetBookingData(string sessionId);
        void RemoveBookingData(string sessionId);

        /// <summary>Serialises find-or-create per user so two genuinely concurrent prepares
        /// can't both miss the reuse check. Dispose to release.</summary>
        Task<IDisposable> AcquirePrepareLockAsync(int userId, CancellationToken cancellationToken = default);

        /// <summary>The live, unconsumed, unexpired prepare for this user and fingerprint, or
        /// null. May be returned with PaymentIntentId still null — a prepare whose Stripe call
        /// failed or timed out. Reusing that session id as the idempotency key is what makes the
        /// retry safe. Call inside the prepare lock.</summary>
        PreparedBookingSession? FindOutstandingPreparedSession(int userId, string fingerprint);

        void StorePreparedSession(PreparedBookingSession session);
    }

    /// <summary>
    /// Identifies "the same logical booking attempt" across repeated prepare-payment calls.
    /// Computed AFTER the server has normalised the DTO (gift card draw clamped to the real
    /// balance, discounts server-resolved), so two attempts at the same booking hash equal even
    /// though the raw client payloads may not have.
    /// </summary>
    public static class BookingSessionFingerprint
    {
        public static string Compute(CreateBookingDto dto, decimal total)
        {
            // Whole-DTO hash rather than a hand-listed field set: a field added to the booking
            // form later is then covered automatically instead of silently widening what counts
            // as "the same attempt".
            var payload = JsonSerializer.Serialize(dto) + "|total=" + total.ToString("F2");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash);
        }
    }

    /// <summary>
    /// NOTE: in-process singleton. Prepare-session reuse therefore protects a single API
    /// instance only — it would not survive scaling out to a second process, where the unique
    /// index on Order.PaymentIntentId remains the backstop. Fine for the current single-process
    /// deployment; revisit if this ever runs behind more than one instance.
    /// </summary>
    public class BookingDataService : IBookingDataService
    {
        /// <summary>How long an unconsumed prepare stays reusable. Long enough to cover a
        /// customer hesitating, navigating away and coming back; short enough that a stale
        /// intent priced against an older gift card balance is never revived. Comfortably inside
        /// Stripe's 24h idempotency-key window, so a reused session's key is always still live.
        /// </summary>
        private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(20);

        private readonly ConcurrentDictionary<string, PreparedBookingSession> _bookingData = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _prepareLocks = new();
        private readonly ILogger<BookingDataService> _logger;

        public BookingDataService(ILogger<BookingDataService> logger)
        {
            _logger = logger;
        }

        public void StoreBookingData(string sessionId, CreateBookingDto bookingData)
        {
            _bookingData[sessionId] = new PreparedBookingSession
            {
                SessionId = sessionId,
                BookingData = bookingData,
                CreatedAtUtc = DateTime.UtcNow
            };
            _logger.LogInformation($"Stored booking data for session {sessionId}");
        }

        public CreateBookingDto GetBookingData(string sessionId)
        {
            _bookingData.TryGetValue(sessionId, out var data);
            return data?.BookingData;
        }

        public void RemoveBookingData(string sessionId)
        {
            _bookingData.TryRemove(sessionId, out _);
            _logger.LogInformation($"Removed booking data for session {sessionId}");
        }

        public async Task<IDisposable> AcquirePrepareLockAsync(int userId, CancellationToken cancellationToken = default)
        {
            var gate = _prepareLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new Releaser(gate);
        }

        public PreparedBookingSession? FindOutstandingPreparedSession(int userId, string fingerprint)
        {
            PruneExpired();

            foreach (var entry in _bookingData.Values)
            {
                if (entry.UserId != userId) continue;
                // Sessions stored by the other booking flows carry no fingerprint; never match
                // them (a real fingerprint is always a non-empty SHA-256 hex string).
                if (string.IsNullOrEmpty(entry.Fingerprint)) continue;
                if (entry.Fingerprint != fingerprint) continue;
                if (IsExpired(entry)) continue;
                return entry;
            }

            return null;
        }

        public void StorePreparedSession(PreparedBookingSession session)
        {
            // Marked here rather than by the caller, so every prepare-payment session is
            // prunable and nothing else ever is, whatever the caller forgot to set.
            session.IsPrepareSession = true;
            _bookingData[session.SessionId] = session;
            _logger.LogInformation($"Stored booking data for session {session.SessionId}");
        }

        /// <summary>Only a prepare-payment attempt can expire — see IsPrepareSession.</summary>
        private static bool IsExpired(PreparedBookingSession session)
            => session.IsPrepareSession && DateTime.UtcNow - session.CreatedAtUtc > SessionTtl;

        /// <summary>
        /// Opportunistic sweep of ABANDONED PREPARE-PAYMENT ATTEMPTS ONLY, which would otherwise
        /// accumulate for the life of the process (they are removed only when a booking succeeds).
        ///
        /// IsExpired returns false for anything that is not a prepare session, so the
        /// `booking_{orderId}_{userId}` entries CreateBooking and create-for-user rely on are
        /// never touched here. Those legitimately live for days — an admin "Send Payment" order
        /// is paid from an emailed link long after it was created — and sweeping them cost the
        /// customer their points, credits, photos and save-card choice at confirm time.
        /// </summary>
        private void PruneExpired()
        {
            foreach (var entry in _bookingData)
            {
                if (IsExpired(entry.Value))
                    _bookingData.TryRemove(entry.Key, out _);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _gate;
            private bool _released;

            public Releaser(SemaphoreSlim gate) => _gate = gate;

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                _gate.Release();
            }
        }
    }
}
