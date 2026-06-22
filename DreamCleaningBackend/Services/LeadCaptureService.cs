using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class LeadCaptureService : ILeadCaptureService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<LeadCaptureService> _logger;

        public LeadCaptureService(ApplicationDbContext context, ILogger<LeadCaptureService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Lead?> CaptureAsync(
            string source,
            string? firstName,
            string? lastName,
            string? email,
            string? phone,
            string? serviceAddress = null,
            string? cleaningType = null,
            string? message = null)
        {
            try
            {
                if (!LeadSource.IsValid(source)) source = LeadSource.Manual;

                var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
                var normalizedPhone = PhoneHelper.NormalizeToDigits(phone);

                // De-dupe: fold repeat inquiries into an existing OPEN lead (not Won/Lost)
                // so the same prospect submitting twice doesn't spawn duplicate cards.
                Lead? existing = null;
                if (normalizedEmail != null || normalizedPhone != null)
                {
                    existing = await _context.Leads
                        .Where(l => l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost)
                        .Where(l =>
                            (normalizedEmail != null && l.Email != null && l.Email.ToLower() == normalizedEmail) ||
                            (normalizedPhone != null && l.Phone == normalizedPhone))
                        .OrderByDescending(l => l.LastActivityAt)
                        .FirstOrDefaultAsync();
                }

                if (existing != null)
                {
                    existing.LastActivityAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    // Backfill any details we didn't have before.
                    if (string.IsNullOrWhiteSpace(existing.ServiceAddress) && !string.IsNullOrWhiteSpace(serviceAddress))
                        existing.ServiceAddress = serviceAddress.Trim();
                    if (string.IsNullOrWhiteSpace(existing.CleaningType) && !string.IsNullOrWhiteSpace(cleaningType))
                        existing.CleaningType = cleaningType.Trim();

                    _context.LeadActivities.Add(new LeadActivity
                    {
                        LeadId = existing.Id,
                        Type = LeadActivityType.System,
                        Content = BuildCaptureSummary(source, message, repeat: true),
                        AdminName = "System",
                        CreatedAt = DateTime.UtcNow
                    });

                    await _context.SaveChangesAsync();
                    return existing;
                }

                var lead = new Lead
                {
                    FirstName = string.IsNullOrWhiteSpace(firstName) ? null : firstName.Trim(),
                    LastName = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim(),
                    Email = normalizedEmail,
                    Phone = normalizedPhone,
                    ServiceAddress = string.IsNullOrWhiteSpace(serviceAddress) ? null : serviceAddress.Trim(),
                    CleaningType = string.IsNullOrWhiteSpace(cleaningType) ? null : cleaningType.Trim(),
                    Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                    Stage = LeadStage.New,
                    Source = source,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow
                };

                _context.Leads.Add(lead);
                await _context.SaveChangesAsync();

                _context.LeadActivities.Add(new LeadActivity
                {
                    LeadId = lead.Id,
                    Type = LeadActivityType.System,
                    Content = BuildCaptureSummary(source, message, repeat: false),
                    AdminName = "System",
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                return lead;
            }
            catch (Exception ex)
            {
                // Inbound capture must never break the public contact/quote flow.
                _logger.LogError(ex, "Failed to capture lead from source {Source}", source);
                return null;
            }
        }

        private static string BuildCaptureSummary(string source, string? message, bool repeat)
        {
            var label = source switch
            {
                LeadSource.ContactForm => "contact form",
                LeadSource.QuoteRequest => "free-quote request",
                LeadSource.LiveChat => "live chat",
                LeadSource.Booking => "booking flow",
                _ => "manual entry"
            };
            var prefix = repeat ? $"Repeat inquiry via {label}" : $"Lead captured from {label}";
            return string.IsNullOrWhiteSpace(message) ? prefix : $"{prefix}: {message.Trim()}";
        }
    }
}
