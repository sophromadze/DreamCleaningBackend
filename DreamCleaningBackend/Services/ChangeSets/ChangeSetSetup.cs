namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// DI for the shared Services-tab logic, change sets (preview → apply), revert and change
    /// history (2026-10). One method so Program.cs and the integration test hosts wire it the same.
    /// The history capture itself needs no registration: ApplicationDbContext adds the
    /// interceptor to every context.
    /// </summary>
    public static class ChangeSetSetup
    {
        public static void AddChangeSets(IServiceCollection services)
        {
            services.AddScoped<CatalogAdminOperations>();
            services.AddScoped<ChangeSetService>();
            services.AddScoped<CatalogSnapshotReader>();
            services.AddScoped<ChangeHistoryReader>();
            services.AddScoped<IChangeSetRunner, CatalogChangeRunner>();
            services.AddScoped<IChangeSetRunner, AddExpenseRunner>();
            services.AddScoped<IChangeSetRunner, DeleteExpenseRunner>();
            services.AddScoped<IChangeSetRunner, RevertRunner>();
        }
    }
}
