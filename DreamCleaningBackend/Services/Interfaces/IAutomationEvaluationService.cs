namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Evaluates CRM retention-automation rules. Pure analysis + alert creation — it never
    /// contacts customers. Shared by the background worker and the manual "run now" endpoint.
    /// </summary>
    public interface IAutomationEvaluationService
    {
        /// <summary>Create the built-in rules (disabled) if they don't exist yet.</summary>
        Task EnsureDefaultRulesAsync();

        /// <summary>Evaluate one rule by key and create alerts. Returns the number of new alerts created.
        /// A disabled rule evaluates to 0. Updates the rule's LastRunAt/LastRunCreatedCount.</summary>
        Task<int> EvaluateRuleAsync(string ruleKey, bool ignoreEnabledFlag = false);

        /// <summary>Evaluate every enabled rule (used by the background worker).</summary>
        Task<int> EvaluateAllEnabledAsync();

        /// <summary>Flip any Snoozed alert whose RemindAt has passed back to Open. Returns how many woke.
        /// Cheap to call — used by the worker and lazily before the admin reads the alert feed.</summary>
        Task<int> WakeDueSnoozedAlertsAsync();
    }
}
