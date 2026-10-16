using System.Text.Json;
using DreamCleaningBackend.Controllers;
using ModelContextProtocol.Protocol;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>
    /// Registers and maps the Claude connector's MCP server (2026-10) — kept out of Program.cs so
    /// the integration tests can build the very same server.
    /// </summary>
    public static class McpServerSetup
    {
        /// <summary>Authorization policy of <c>/api/mcp</c>: the McpBearer scheme ONLY.</summary>
        public const string Policy = "McpConnector";

        public static void AddConnectorMcpServer(IServiceCollection services)
        {
            services.AddHttpContextAccessor();
            services.AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = McpToolCatalog.ServerName,
                        Title = McpToolCatalog.ServerTitle,
                        Version = "1.0.0"
                    };
                    options.ServerInstructions = McpToolCatalog.Instructions;
                })
                // Stateless: no Mcp-Session-Id, no GET stream, nothing held in memory between calls.
                .WithHttpTransport(options => options.Stateless = true)
                .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = McpToolCatalog.Tools.ToList() }))
                .WithCallToolHandler(async (request, ct) =>
                {
                    var http = request.Services?.GetRequiredService<IHttpContextAccessor>().HttpContext;
                    var services = http?.RequestServices ?? request.Services
                        ?? throw new InvalidOperationException("No request services for the MCP call.");
                    var caller = http?.Items[typeof(McpCaller)] as McpCaller;
                    var args = JsonSerializer.SerializeToElement(
                        request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
                    var executor = services.GetRequiredService<McpToolExecutor>();
                    return await executor.CallAsync(caller, request.Params?.Name ?? string.Empty, args, ct);
                });
        }

        public static void MapConnector(IEndpointRouteBuilder app)
        {
            app.MapMcp(McpOAuthPolicy.McpPath)
                .RequireAuthorization(Policy)
                .RequireCors(McpOAuthController.CorsPolicy);
        }
    }
}
