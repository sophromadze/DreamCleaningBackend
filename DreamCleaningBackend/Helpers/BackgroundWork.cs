namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Runs best-effort work — a confirmation email, an SMS, a rewards grant — AFTER the caller
    /// has stopped waiting, and with a DbContext of its own.
    ///
    /// WHY THIS EXISTS (2026-09-16). A customer was charged twice for one booking. The first
    /// confirm-payment created the order, charged the card, then threw
    /// "A second operation was started on this context instance before a previous operation
    /// completed" — because the confirmation email, the confirmation SMS and the company
    /// notification had each been launched with a bare `_ = Task.Run(...)` that captured the
    /// controller's SCOPED IEmailService / ISmsService. Those two share the request's
    /// ApplicationDbContext, and the first thing either of them does is query it (the blocked-user
    /// suppression gate in EmailService / SmsService). So three background DB queries were racing
    /// whatever the request did next. The customer read the error as a failed payment and clicked
    /// Pay again.
    ///
    /// The rule this states once: **fire-and-forget work never borrows the caller's scope.** It
    /// gets its own, which means its own DbContext, which means it cannot collide with the
    /// request — and it also survives the request scope being disposed the moment the response is
    /// written, which a captured scoped service does not.
    ///
    /// Two things the caller must still get right, because no helper can enforce them:
    ///   * Capture VALUES, never tracked entities. An EF entity belongs to the context that
    ///     loaded it and its change tracker is not thread-safe; read what you need into locals
    ///     first (`var serviceDate = order.ServiceDate;`) and close over those.
    ///   * Nothing in here may be load-bearing. Exceptions are logged and swallowed: the work is
    ///     detached, so there is nobody left to report a failure to.
    /// </summary>
    public static class BackgroundWork
    {
        /// <summary>
        /// Starts <paramref name="work"/> on the thread pool with a fresh DI scope.
        /// <paramref name="description"/> is what the log line names when it fails.
        /// </summary>
        public static void Run(
            IServiceScopeFactory? scopeFactory,
            ILogger logger,
            string description,
            Func<IServiceProvider, Task> work)
        {
            if (scopeFactory == null)
            {
                // Only reachable from a hand-constructed instance (a unit test that did not wire
                // the factory). Skipped rather than thrown: this work is best-effort by
                // definition, and throwing here would fail whatever the caller was really doing.
                logger.LogError("Background work '{Description}' skipped: no IServiceScopeFactory was supplied.", description);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await work(scope.ServiceProvider);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background work '{Description}' failed", description);
                }
            });
        }
    }
}
