namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Resolves and persists the set of restricted admin pages a user has been granted
    /// read-only ("view") access to. Page keys come from
    /// <see cref="DreamCleaningBackend.Services.AdminViewablePages"/>.
    /// </summary>
    public interface IPageAccessService
    {
        /// <summary>Returns the granted page keys for the user (empty when none or not an Admin).</summary>
        Task<HashSet<string>> GetGrantedPagesAsync(int userId);

        /// <summary>Parses the raw JSON column value into a normalized list of valid page keys.</summary>
        List<string> ParsePages(string? raw);

        /// <summary>
        /// Replaces the user's granted pages. Only valid keys are kept. Grants are only meaningful
        /// for the Admin role; throws <see cref="InvalidOperationException"/> for other roles.
        /// Returns the normalized list that was persisted. Does not write audit logs — callers
        /// own auditing (mirrors the role-update flow).
        /// </summary>
        Task<List<string>> SetGrantedPagesAsync(int userId, IEnumerable<string> pages);
    }
}
