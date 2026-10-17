using DreamCleaningBackend.Services.Mcp;

namespace DreamCleaningBackend.Services.ChangeSets
{
    /// <summary>
    /// add_expense as a change set (2026-10): the connector's existing <see cref="McpExpenseTools.AddAsync"/>
    /// — its refusals, duplicate guard and ExpenseService validation — run once as a rolled-back
    /// preview and again on apply. Nothing about the rules changed; only that the write waits for
    /// the user's confirmation.
    /// </summary>
    public sealed class AddExpenseRunner : IChangeSetRunner
    {
        public string Kind => ChangeSetKinds.AddExpense;

        public async Task<ChangeRunResult> RunAsync(ChangeRunContext context, CancellationToken ct)
        {
            if (!context.Caller.ViaConnector)
                throw new ChangeSetException("Expenses are added on the Expenses page.");
            var tools = context.Services.GetRequiredService<McpExpenseTools>();
            var added = await tools.AddAsync(context.Request, context.Caller.UserId, context.Caller.McpGrantId!.Value);
            var result = new ChangeRunResult { Result = added };
            if (context.IsPreview) result.Warnings.Add("The expense id shown is provisional; the real one is assigned when the change is applied.");
            return result;
        }
    }

    /// <summary>delete_expense as a change set: <see cref="McpExpenseTools.DeleteAsync"/> and its rules, previewed first.</summary>
    public sealed class DeleteExpenseRunner : IChangeSetRunner
    {
        public string Kind => ChangeSetKinds.DeleteExpense;

        public async Task<ChangeRunResult> RunAsync(ChangeRunContext context, CancellationToken ct)
        {
            if (!context.Caller.ViaConnector)
                throw new ChangeSetException("Expenses are deleted on the Expenses page.");
            var tools = context.Services.GetRequiredService<McpExpenseTools>();
            return new ChangeRunResult { Result = await tools.DeleteAsync(context.Request, context.Caller.UserId) };
        }
    }
}
