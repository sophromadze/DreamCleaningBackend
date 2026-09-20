using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Billing;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Billing
{
    /// <summary>The authorisation a charge is being made under, once every gate has passed.</summary>
    public class EffectiveAuthorization
    {
        public PaymentAuthorization Authorization { get; set; } = null!;
        public bool AllowBackupFallback => Authorization.AllowBackupFallback;
    }

    public interface IPaymentAuthorizationService
    {
        Task<AutoPayOverviewDto> GetOverviewAsync(int userId);
        Task<AutoPayTermsDto> GetTermsAsync(int userId, string scope, int? seriesId, int? clientId, bool allowBackup);
        Task<AutoPayOverviewDto> EnableAutoPayAsync(int userId, EnableAutoPayDto dto, string? ip, string? userAgent);
        Task<AutoPayOverviewDto> DisableAutoPayAsync(int userId);
        Task<AutoPayOverviewDto> AuthorizeAsync(int userId, AuthorizeArrangementDto dto, string? ip, string? userAgent);
        Task<AutoPayOverviewDto> RevokeAsync(int userId, int authorizationId);

        /// <summary>
        /// The authorisation that lets a charge happen right now, or null. Null whenever ANY
        /// gate is shut: the feature switch, the customer's master switch, the General agreement,
        /// or the arrangement's own authorisation.
        /// </summary>
        Task<EffectiveAuthorization?> ResolveEffectiveAsync(int userId, PaymentAuthorizationScope scope,
            int? seriesId = null, int? clientId = null);
    }

    /// <summary>
    /// Who has agreed to what. See <see cref="PaymentAuthorization"/> for the record itself and
    /// <see cref="AutoPayTerms"/> for the wording.
    ///
    /// ══ WHAT MUST BE TRUE FOR A CHARGE ══
    ///   Billing:AutoPayEnabled (server) → the customer's master switch → an active General
    ///   agreement → an active authorisation for THIS arrangement → a usable Primary card.
    /// Every one of those is re-checked at charge time by <see cref="ResolveEffectiveAsync"/>; the
    /// Billing tab only describes them.
    ///
    /// The office-booked-orders scope is the one exception to the feature switch that matters
    /// here: it authorises an ADMIN's button, which is governed by Billing:SavedCardsEnabled, so it
    /// can be granted without AutoPay being rolled out.
    /// </summary>
    public class PaymentAuthorizationService : IPaymentAuthorizationService
    {
        private readonly ApplicationDbContext _context;
        private readonly BillingFeatures _features;
        private readonly IAuditService _audit;
        private readonly ILogger<PaymentAuthorizationService> _logger;

        public PaymentAuthorizationService(
            ApplicationDbContext context,
            BillingFeatures features,
            IAuditService audit,
            ILogger<PaymentAuthorizationService> logger)
        {
            _context = context;
            _features = features;
            _audit = audit;
            _logger = logger;
        }

        // ── Reading ───────────────────────────────────────────────────────────────────────────

        public async Task<AutoPayOverviewDto> GetOverviewAsync(int userId)
        {
            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new BillingRuleException("We couldn't find your account.");

            var cards = await _context.CustomerPaymentMethods.AsNoTracking()
                .Where(c => c.UserId == userId && (c.Id == user.PrimaryPaymentMethodId || c.Id == user.BackupPaymentMethodId))
                .ToListAsync();
            var primary = cards.FirstOrDefault(c => c.Id == user.PrimaryPaymentMethodId);
            var backup = cards.FirstOrDefault(c => c.Id == user.BackupPaymentMethodId);
            var primaryUsable = primary != null && primary.Status == CustomerPaymentMethodStatus.Active
                                && !primary.IsExpiredAt(NyTimeHelper.NowNy);

            var active = await _context.PaymentAuthorizations.AsNoTracking()
                .Where(a => a.UserId == userId && a.Status == PaymentAuthorizationStatus.Active)
                .ToListAsync();
            var general = active.FirstOrDefault(a => a.Scope == PaymentAuthorizationScope.General);

            var overview = new AutoPayOverviewDto
            {
                FeatureEnabled = _features.AutoPayEnabled,
                AutoPayEnabled = user.AutoPayEnabled && general != null,
                AutoPayEnabledAt = general?.AcceptedAt,
                PrimaryCard = primary == null ? null
                    : PaymentMethodService.ToDto(primary, user.PrimaryPaymentMethodId, user.BackupPaymentMethodId, false),
                BackupCard = backup == null ? null
                    : PaymentMethodService.ToDto(backup, user.PrimaryPaymentMethodId, user.BackupPaymentMethodId, false)
            };

            string? PausedReason(PaymentAuthorization? auth, bool needsAutoPayFeature)
            {
                if (auth == null) return null;
                if (needsAutoPayFeature && !_features.AutoPayEnabled) return "Automatic payments are not available yet.";
                if (!needsAutoPayFeature && !_features.SavedCardsEnabled) return "Saved-card payments are not available yet.";
                // The office scope authorises a person's button, not an automatic charge, so the
                // master switch does not pause it.
                if (needsAutoPayFeature && !overview.AutoPayEnabled) return "Paused because Automatic Payments are turned off.";
                if (!primaryUsable) return "Paused because you have no usable Primary card.";
                return null;
            }

            AutoPayArrangementDto Describe(AutoPayArrangementDto dto, PaymentAuthorization? auth, bool needsAutoPayFeature)
            {
                dto.IsAuthorized = auth != null;
                dto.AuthorizationId = auth?.Id;
                dto.AuthorizedAt = auth?.AcceptedAt;
                dto.TermsVersion = auth?.TermsVersion;
                dto.AllowBackupFallback = auth?.AllowBackupFallback ?? false;
                dto.PausedReason = PausedReason(auth, needsAutoPayFeature);
                dto.IsEffective = auth != null && dto.PausedReason == null;
                return dto;
            }

            // Recurring series the customer has — active ones, plus any with a still-active
            // authorisation so a paused/stopped series' authorisation can still be revoked.
            var seriesAuthIds = active.Where(a => a.Scope == PaymentAuthorizationScope.RecurringSeries)
                .Select(a => a.RecurringSeriesId).ToHashSet();
            var series = await _context.RecurringOrderSeries.AsNoTracking()
                .Include(s => s.TemplateOrder)
                .Where(s => s.UserId == userId && ((s.IsActive && s.StoppedAt == null) || seriesAuthIds.Contains(s.Id)))
                .OrderBy(s => s.Id)
                .ToListAsync();

            foreach (var s in series)
            {
                var auth = active.FirstOrDefault(a => a.Scope == PaymentAuthorizationScope.RecurringSeries && a.RecurringSeriesId == s.Id);
                var running = s.IsActive && s.StoppedAt == null;
                var dto = Describe(new AutoPayArrangementDto
                {
                    Scope = "series",
                    RecurringSeriesId = s.Id,
                    Title = "Recurring cleaning — " + DescribeSeries(s),
                    Description = "Your saved card pays for one cleaning at a time in this recurring plan.",
                    Timing = "Charged when payment for the next cleaning is due — at least 24 hours after the previous cleaning has finished. Never several at once."
                }, auth, needsAutoPayFeature: true);
                if (!running && auth != null)
                {
                    dto.PausedReason = "This recurring plan is no longer active.";
                    dto.IsEffective = false;
                }
                overview.Arrangements.Add(dto);
            }

            overview.Arrangements.Add(Describe(new AutoPayArrangementDto
            {
                Scope = "office",
                Title = "Orders our office books for you",
                Description = "Lets our staff charge your Primary card for an order they book for you (for example by phone), when you ask them to. Without this we send you a payment link instead.",
                Timing = "Only when a staff member charges a specific order at your request — never automatically."
            }, active.FirstOrDefault(a => a.Scope == PaymentAuthorizationScope.OfficeBookedOrders), needsAutoPayFeature: false));

            // Business clients linked to this account.
            var clients = await _context.ContractClients.AsNoTracking()
                .Where(c => c.SourceUserId == userId && c.IsActive)
                .OrderBy(c => c.LegalEntityName)
                .Select(c => new { c.Id, c.LegalEntityName })
                .ToListAsync();
            foreach (var c in clients)
            {
                overview.Arrangements.Add(Describe(new AutoPayArrangementDto
                {
                    Scope = "client",
                    ContractClientId = c.Id,
                    Title = $"Invoices for {c.LegalEntityName}",
                    Description = "Pays each invoice's remaining balance with your Primary card on its due date. Your payment terms do not change.",
                    Timing = "On each invoice's due date, once per invoice. Invoices already paid or with a bank payment settling are never charged."
                }, active.FirstOrDefault(a => a.Scope == PaymentAuthorizationScope.CommercialClient && a.ContractClientId == c.Id),
                    needsAutoPayFeature: true));
            }

            return overview;
        }

        public async Task<AutoPayTermsDto> GetTermsAsync(int userId, string scope, int? seriesId, int? clientId, bool allowBackup)
        {
            var parsed = ParseScope(scope);
            var context = await BuildTermsContextAsync(userId, parsed, seriesId, clientId, allowBackup);
            return new AutoPayTermsDto
            {
                Scope = scope,
                Version = AutoPayTerms.Version,
                Text = AutoPayTerms.Render(parsed, context)
            };
        }

        // ── The master switch ─────────────────────────────────────────────────────────────────

        public async Task<AutoPayOverviewDto> EnableAutoPayAsync(int userId, EnableAutoPayDto dto, string? ip, string? userAgent)
        {
            if (!_features.AutoPayEnabled)
                throw new BillingRuleException("Automatic payments are not available yet.");
            if (!dto.AcceptTerms)
                throw new BillingRuleException("Please read and accept the automatic payment terms to turn this on.");
            if (dto.TermsVersion != AutoPayTerms.Version)
                throw new BillingRuleException("The terms were updated while this page was open. Please review them again.", "terms_changed");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new BillingRuleException("We couldn't find your account.");

            var primary = user.PrimaryPaymentMethodId == null ? null
                : await _context.CustomerPaymentMethods.FirstOrDefaultAsync(c => c.Id == user.PrimaryPaymentMethodId);
            if (primary == null || primary.Status != CustomerPaymentMethodStatus.Active || primary.IsExpiredAt(NyTimeHelper.NowNy))
                throw new BillingRuleException("Add a valid card and make it your Primary card before turning on Automatic Payments.",
                    "no_primary_card");

            var existing = await _context.PaymentAuthorizations.AnyAsync(a =>
                a.UserId == userId && a.Scope == PaymentAuthorizationScope.General && a.Status == PaymentAuthorizationStatus.Active);

            if (!existing)
            {
                var text = AutoPayTerms.Render(PaymentAuthorizationScope.General, new AutoPayTermsContext());
                _context.PaymentAuthorizations.Add(NewAuthorization(userId, PaymentAuthorizationScope.General, null, null,
                    allowBackup: false, text, ip, userAgent));
            }

            user.AutoPayEnabled = true;
            user.AutoPayUpdatedAt = DateTime.UtcNow;
            await SaveUniqueAsync();

            await AuditAsync(userId, "AutoPayEnabled", new { TermsVersion = AutoPayTerms.Version, PrimaryCard = PaymentMethodService.Label(primary) });
            return await GetOverviewAsync(userId);
        }

        public async Task<AutoPayOverviewDto> DisableAutoPayAsync(int userId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new BillingRuleException("We couldn't find your account.");

            var general = await _context.PaymentAuthorizations
                .Where(a => a.UserId == userId && a.Scope == PaymentAuthorizationScope.General && a.Status == PaymentAuthorizationStatus.Active)
                .ToListAsync();
            foreach (var row in general) Revoke(row, userId, "Customer turned Automatic Payments off.");

            // The arrangement authorisations stay on record but are PAUSED: every charge path
            // re-checks the master switch. Turning AutoPay back on asks for the general agreement
            // again, and resumes them.
            user.AutoPayEnabled = false;
            user.AutoPayUpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await AuditAsync(userId, "AutoPayDisabled", new { RevokedGeneralAuthorizations = general.Count });
            return await GetOverviewAsync(userId);
        }

        // ── Arrangements ──────────────────────────────────────────────────────────────────────

        public async Task<AutoPayOverviewDto> AuthorizeAsync(int userId, AuthorizeArrangementDto dto, string? ip, string? userAgent)
        {
            var scope = ParseScope(dto.Scope);
            if (scope == PaymentAuthorizationScope.General)
                throw new BillingRuleException("Use the Automatic Payments switch for the general agreement.");

            if (scope == PaymentAuthorizationScope.OfficeBookedOrders)
            {
                if (!_features.SavedCardsEnabled)
                    throw new BillingRuleException("Saved-card payments are not available yet.");
                if (!dto.SmsConsent || !dto.CancellationFeeConsent || !dto.TermsOfServiceConsent)
                    throw new BillingRuleException("Please tick all three agreements — they apply to every order our office books for you.",
                        "consents_required");
            }
            else if (!_features.AutoPayEnabled)
            {
                throw new BillingRuleException("Automatic payments are not available yet.");
            }

            if (!dto.AcceptTerms)
                throw new BillingRuleException("Please read and accept the authorization to continue.");
            if (dto.TermsVersion != AutoPayTerms.Version)
                throw new BillingRuleException("The terms were updated while this page was open. Please review them again.", "terms_changed");

            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new BillingRuleException("We couldn't find your account.");

            // Automatic arrangements need the general agreement first; the office scope authorises
            // a person's button, not an automatic charge, so it does not.
            if (scope != PaymentAuthorizationScope.OfficeBookedOrders && !user.AutoPayEnabled)
                throw new BillingRuleException("Turn on Automatic Payments first.", "autopay_off");

            if (user.PrimaryPaymentMethodId == null)
                throw new BillingRuleException("Add a card and make it your Primary card first.", "no_primary_card");

            var allowBackup = dto.AllowBackupFallback;
            if (allowBackup && user.BackupPaymentMethodId == null)
                throw new BillingRuleException("Choose a Backup card before allowing Backup card payments.", "no_backup_card");

            int? seriesId = null, clientId = null;
            if (scope == PaymentAuthorizationScope.RecurringSeries)
            {
                seriesId = dto.RecurringSeriesId ?? throw new BillingRuleException("Choose a recurring plan.");
                var owned = await _context.RecurringOrderSeries.AnyAsync(s =>
                    s.Id == seriesId && s.UserId == userId && s.IsActive && s.StoppedAt == null);
                if (!owned) throw new BillingRuleException("That recurring plan was not found or is no longer active.");
            }
            else if (scope == PaymentAuthorizationScope.CommercialClient)
            {
                clientId = dto.ContractClientId ?? throw new BillingRuleException("Choose a business account.");
                // Ownership is the existing business-account link, and nothing else: a business
                // client's invoices can only be authorised by the account that client is linked to.
                var linked = await _context.ContractClients.AnyAsync(c =>
                    c.Id == clientId && c.SourceUserId == userId && c.IsActive);
                if (!linked) throw new BillingRuleException("That business account is not linked to your profile.");
            }

            var context = await BuildTermsContextAsync(userId, scope, seriesId, clientId, allowBackup);
            var text = AutoPayTerms.Render(scope, context);
            var scopeKey = PaymentAuthorization.BuildScopeKey(scope, seriesId, clientId);

            // Re-authorising REPLACES the active row (e.g. switching Backup fallback on), and the
            // old row stays as history. Both writes in one save, so there is never a moment with
            // two active rows — the unique index would refuse one anyway.
            var current = await _context.PaymentAuthorizations
                .Where(a => a.UserId == userId && a.ActiveScopeKey == scopeKey)
                .ToListAsync();
            foreach (var row in current) Revoke(row, userId, "Replaced by a new authorization.");
            if (current.Count > 0) await _context.SaveChangesAsync();

            var authorization = NewAuthorization(userId, scope, seriesId, clientId, allowBackup, text, ip, userAgent);
            if (scope == PaymentAuthorizationScope.OfficeBookedOrders)
            {
                authorization.SmsConsentAccepted = true;
                authorization.CancellationFeeAccepted = true;
                authorization.TermsOfServiceAccepted = true;
            }
            _context.PaymentAuthorizations.Add(authorization);
            await SaveUniqueAsync();

            await AuditAsync(userId, "ArrangementAuthorized", new
            {
                AuthorizationId = authorization.Id,
                Scope = scope.ToString(),
                RecurringSeriesId = seriesId,
                ContractClientId = clientId,
                AllowBackupFallback = allowBackup,
                TermsVersion = AutoPayTerms.Version
            });

            return await GetOverviewAsync(userId);
        }

        public async Task<AutoPayOverviewDto> RevokeAsync(int userId, int authorizationId)
        {
            var row = await _context.PaymentAuthorizations
                .FirstOrDefaultAsync(a => a.Id == authorizationId && a.UserId == userId)
                ?? throw new BillingRuleException("That authorization was not found.");

            if (row.Status == PaymentAuthorizationStatus.Active)
            {
                if (row.Scope == PaymentAuthorizationScope.General)
                    return await DisableAutoPayAsync(userId);

                Revoke(row, userId, "Revoked by the customer.");
                await _context.SaveChangesAsync();
                await AuditAsync(userId, "ArrangementRevoked", new { AuthorizationId = row.Id, Scope = row.Scope.ToString(),
                    row.RecurringSeriesId, row.ContractClientId });
            }

            return await GetOverviewAsync(userId);
        }

        // ── The gate every charge path calls ──────────────────────────────────────────────────

        public async Task<EffectiveAuthorization?> ResolveEffectiveAsync(int userId, PaymentAuthorizationScope scope,
            int? seriesId = null, int? clientId = null)
        {
            if (scope == PaymentAuthorizationScope.OfficeBookedOrders ? !_features.SavedCardsEnabled : !_features.AutoPayEnabled)
                return null;

            var user = await _context.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.AutoPayEnabled, u.IsActive })
                .FirstOrDefaultAsync();
            if (user == null || !user.IsActive) return null;

            var scopeKey = PaymentAuthorization.BuildScopeKey(scope, seriesId, clientId);
            var authorization = await _context.PaymentAuthorizations.AsNoTracking()
                .FirstOrDefaultAsync(a => a.UserId == userId && a.ActiveScopeKey == scopeKey
                                          && a.Status == PaymentAuthorizationStatus.Active);
            if (authorization == null) return null;

            if (scope != PaymentAuthorizationScope.OfficeBookedOrders)
            {
                if (!user.AutoPayEnabled) return null;
                var general = await _context.PaymentAuthorizations.AsNoTracking().AnyAsync(a =>
                    a.UserId == userId && a.Scope == PaymentAuthorizationScope.General && a.Status == PaymentAuthorizationStatus.Active);
                if (!general) return null;
            }

            // The arrangement itself must still be one this customer owns.
            if (scope == PaymentAuthorizationScope.RecurringSeries)
            {
                var ok = await _context.RecurringOrderSeries.AnyAsync(s =>
                    s.Id == seriesId && s.UserId == userId && s.IsActive && s.StoppedAt == null);
                if (!ok) return null;
            }
            else if (scope == PaymentAuthorizationScope.CommercialClient)
            {
                var ok = await _context.ContractClients.AnyAsync(c => c.Id == clientId && c.SourceUserId == userId && c.IsActive);
                if (!ok) return null;
            }

            return new EffectiveAuthorization { Authorization = authorization };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        public static PaymentAuthorizationScope ParseScope(string? scope) => (scope ?? "").Trim().ToLowerInvariant() switch
        {
            "general" => PaymentAuthorizationScope.General,
            "series" or "recurring" => PaymentAuthorizationScope.RecurringSeries,
            "office" => PaymentAuthorizationScope.OfficeBookedOrders,
            "client" or "commercial" => PaymentAuthorizationScope.CommercialClient,
            _ => throw new BillingRuleException("Unknown payment arrangement.")
        };

        private async Task<AutoPayTermsContext> BuildTermsContextAsync(int userId, PaymentAuthorizationScope scope,
            int? seriesId, int? clientId, bool allowBackup)
        {
            var context = new AutoPayTermsContext { AllowBackupFallback = allowBackup };

            if (scope == PaymentAuthorizationScope.RecurringSeries && seriesId.HasValue)
            {
                var series = await _context.RecurringOrderSeries.AsNoTracking()
                    .Include(s => s.TemplateOrder)
                    .FirstOrDefaultAsync(s => s.Id == seriesId && s.UserId == userId);
                if (series != null) context.SeriesDescription = DescribeSeries(series);
            }
            else if (scope == PaymentAuthorizationScope.CommercialClient && clientId.HasValue)
            {
                context.ClientName = await _context.ContractClients.AsNoTracking()
                    .Where(c => c.Id == clientId && c.SourceUserId == userId)
                    .Select(c => c.LegalEntityName)
                    .FirstOrDefaultAsync();
            }

            return context;
        }

        public static string DescribeSeries(RecurringOrderSeries s)
        {
            var unit = s.IntervalUnit switch
            {
                RecurrenceIntervalUnit.Weeks => s.IntervalValue == 1 ? "week" : "weeks",
                RecurrenceIntervalUnit.Months => s.IntervalValue == 1 ? "month" : "months",
                _ => s.IntervalValue == 1 ? "day" : "days"
            };
            var every = s.IntervalValue == 1 ? $"every {unit}" : $"every {s.IntervalValue} {unit}";
            var address = s.TemplateOrder?.ServiceAddress;
            return string.IsNullOrWhiteSpace(address) ? every : $"{every} at {address}";
        }

        private static PaymentAuthorization NewAuthorization(int userId, PaymentAuthorizationScope scope, int? seriesId, int? clientId,
            bool allowBackup, string text, string? ip, string? userAgent)
        {
            var key = PaymentAuthorization.BuildScopeKey(scope, seriesId, clientId);
            return new PaymentAuthorization
            {
                UserId = userId,
                Scope = scope,
                RecurringSeriesId = seriesId,
                ContractClientId = clientId,
                ScopeKey = key,
                ActiveScopeKey = key,
                AllowBackupFallback = allowBackup,
                TermsVersion = AutoPayTerms.Version,
                TermsSnapshot = text,
                TermsHash = AutoPayTerms.Hash(text),
                AcceptedAt = DateTime.UtcNow,
                AcceptedIp = Truncate(ip, 45),
                AcceptedUserAgent = Truncate(userAgent, 300),
                Status = PaymentAuthorizationStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private static void Revoke(PaymentAuthorization row, int byUserId, string reason)
        {
            row.Status = PaymentAuthorizationStatus.Revoked;
            row.ActiveScopeKey = null;
            row.RevokedAt = DateTime.UtcNow;
            row.RevokedByUserId = byUserId;
            row.RevokedReason = Truncate(reason, 200);
            row.UpdatedAt = DateTime.UtcNow;
        }

        private async Task SaveUniqueAsync()
        {
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // Two clicks racing: the unique (UserId, ActiveScopeKey) index kept exactly one.
                _logger.LogInformation(ex, "Concurrent authorization write refused by the unique index.");
                throw new BillingRuleException("This was just updated from another window. Please refresh.", "concurrent_change");
            }
        }

        private async Task AuditAsync(int userId, string action, object payload)
        {
            try
            {
                await _audit.LogActionAsync(AuditEntityTypes.PaymentAuthorizationAction, userId, action, null, payload,
                    actingUserId: userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not audit authorization action {Action} for user {UserId}.", action, userId);
            }
        }

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
    }
}
