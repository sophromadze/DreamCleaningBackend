using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// THE RULES tying a login ACCOUNT (User) to a CLEANER RECORD (Cleaner), in one pure place so
    /// registration, the one-time backfill, the admin Cleaners tab and the cleaner portal
    /// all answer the same three questions the same way.
    ///
    /// Why two entities at all: cleaners were migrated off the User table long ago (see
    /// Cleaner.MigratedFromUserId) because a cleaner is a person the company staffs onto jobs
    /// whether or not they ever sign in. Orders are assigned to a Cleaner row, never to a User. So
    /// an account is an OPTIONAL attachment to a cleaner, and the portal's whole job is resolving
    /// "which Cleaner row is behind the person looking at this page".
    ///
    /// The email match is DISCOVERY, the FK is TRUTH. Cleaner.Email is admin-editable free text and
    /// is routinely blank or stale; matching on it forever would move somebody's schedule the day a
    /// typo was corrected. So an email match is used only to CREATE the link (on registration, and
    /// by the backfill), and Cleaner.UserId is what every read resolves through afterwards.
    /// </summary>
    public static class CleanerAccountLink
    {
        /// <summary>
        /// A comparable email, or null when there is nothing to compare. Null covers blank values
        /// AND the generated no-email placeholders (see NoEmailHelper) - those are non-routable
        /// strings on a per-account basis, so they can never legitimately match anything, and
        /// treating them as ordinary text would make "no email" look like an identity.
        /// </summary>
        public static string? NormalizeEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var trimmed = email.Trim();
            if (NoEmailHelper.IsPlaceholder(trimmed)) return null;
            return trimmed.ToLowerInvariant();
        }

        /// <summary>Case-insensitive email equality, with blanks and placeholders never matching.</summary>
        public static bool EmailsMatch(string? a, string? b)
        {
            var na = NormalizeEmail(a);
            var nb = NormalizeEmail(b);
            return na != null && nb != null && na == nb;
        }

        /// <summary>
        /// Is this cleaner record's email OWNED by a login account, and therefore NOT editable
        /// from the Cleaners Dashboard?
        ///
        /// Linking deliberately overwrites the record's email with the address the account signs in
        /// with, so that the assignment mail reaches the person who reads the portal. Editing it
        /// afterwards on the dashboard would break exactly that: the FK keeps the portal working, so
        /// nothing visibly fails, while the mail quietly starts going somewhere else. The address to
        /// change is the ACCOUNT's, and re-linking is what copies it across.
        ///
        /// The second half of the condition is not decoration. A linked account with no sendable
        /// address (a no-email cash customer who was promoted) never had its email copied onto the
        /// record, so that record's email is its own contact detail and locking it would strand the
        /// only address anybody has with no way to correct it.
        /// </summary>
        public static bool EmailIsManagedByAccount(int? linkedUserId, string? accountEmail) =>
            linkedUserId.HasValue && NormalizeEmail(accountEmail) != null;

        /// <summary>
        /// May this account be auto-promoted to the Cleaner role because its email matches a cleaner
        /// record? ONLY a plain Customer.
        ///
        /// The exclusions are the point. A SuperAdmin, Admin or Moderator whose address also sits on
        /// a cleaner row (an owner who cleans, an admin who was a cleaner first) must NEVER be
        /// silently demoted into a read-only portal - that is an outage of the admin panel with no
        /// visible cause. And an account already on Cleaner needs no promoting. Anyone who genuinely
        /// belongs in both places is moved by hand from the Cleaners tab.
        /// </summary>
        public static bool CanAutoAssignCleanerRole(UserRole currentRole) =>
            currentRole == UserRole.Customer;

        /// <summary>
        /// True when this role sees the cleaner portal at all: a Cleaner (their own work) plus
        /// Admin and SuperAdmin (the whole schedule). Admins were added in 2026-09 - they are the
        /// people who staff the jobs and chase the day, so a calendar of every cleaning is their
        /// daily work rather than an owner's report.
        ///
        /// Moderator is deliberately NOT here: they are a View-only role who do not run the
        /// schedule, and widening to them is one enum value plus the matching [Authorize] strings.
        /// </summary>
        public static bool CanOpenPortal(UserRole role) =>
            role == UserRole.Cleaner || IsSystemWideRole(role);

        /// <summary>
        /// True when the caller sees EVERY cleaning rather than one person's.
        ///
        /// Named for what it grants, not for a role: it started as "is this a SuperAdmin" and the
        /// moment Admins were added, a predicate called after one of its two roles would have
        /// invited the next reader to assume the other was excluded.
        /// </summary>
        public static bool IsSystemWideRole(UserRole role) =>
            role == UserRole.Admin || role == UserRole.SuperAdmin;

        /// <summary>
        /// True when the caller sees the portal as a CLEANER - their own jobs, minimal detail, no
        /// pricing. An admin is not a cleaner here even if a Cleaner row happens to be linked to
        /// their account: they get the system-wide view.
        /// </summary>
        public static bool IsCleanerView(UserRole role) => role == UserRole.Cleaner;
    }
}
