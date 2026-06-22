using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Single entry point for turning an inbound inquiry (contact form, free-quote,
    /// live chat) into a CRM <see cref="Lead"/>. Owns the de-duplication rule so the
    /// pipeline doesn't fill with duplicate cards when the same prospect submits twice.
    /// Inbound capture must never throw into the caller's flow — failures are swallowed
    /// and logged so a CRM hiccup can't break the public contact/quote endpoints.
    /// </summary>
    public interface ILeadCaptureService
    {
        /// <summary>
        /// Capture (or merge into) a lead from an inbound source. If an OPEN lead
        /// (not Won/Lost) already exists for the same email or phone, a timeline entry is
        /// appended to it instead of creating a duplicate. Returns the lead, or null if
        /// capture failed (never throws).
        /// </summary>
        Task<Lead?> CaptureAsync(
            string source,
            string? firstName,
            string? lastName,
            string? email,
            string? phone,
            string? serviceAddress = null,
            string? cleaningType = null,
            string? message = null);
    }
}
