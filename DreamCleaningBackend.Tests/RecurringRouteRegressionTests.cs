using System.Diagnostics;
using DreamCleaningBackend.Controllers.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamCleaningBackend.Tests;

public class RecurringRouteRegressionTests
{
    [Theory]
    [InlineData("POST", "/7/state/pause", "SetState", "pause")]
    [InlineData("POST", "/7/state/resume", "SetState", "resume")]
    [InlineData("POST", "/7/state/stop", "SetState", "stop")]
    [InlineData("POST", "/7/generate", "Generate", null)]
    [InlineData("POST", "/7/occurrences/8/skip", "Skip", null)]
    [InlineData("PUT", "/7", "Update", null)]
    [InlineData("POST", "/from-order/8", "CreateFromOrder", null)]
    [InlineData("POST", "/from-order/8/preview", "Preview", null)]
    public async Task ActualAspNetRoutingMatchesLifecycleUrls(string method, string path, string action, string? state)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddRouting();
        services.AddSingleton<DiagnosticListener>(new DiagnosticListener("RecurringRouteTests"));
        services.AddSingleton<DiagnosticSource>(p => p.GetRequiredService<DiagnosticListener>());
        services.AddControllers().AddApplicationPart(typeof(AdminRecurringOrdersController).Assembly);
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseRouting();
        // Stop after real endpoint selection, without starting a host, DB, background jobs or sending anything.
        Endpoint? selected = null;
        app.Use(next => context => { selected = context.GetEndpoint(); return Task.CompletedTask; });
        app.UseEndpoints(endpoints => endpoints.MapControllers());
        var request = new DefaultHttpContext { RequestServices = provider };
        request.Request.Method = method; request.Request.Path = "/api/admin/recurring-series" + path;
        await app.Build()(request);
        var descriptor = selected?.Metadata.GetMetadata<ControllerActionDescriptor>();
        Assert.NotNull(descriptor); Assert.Equal(typeof(AdminRecurringOrdersController), descriptor.ControllerTypeInfo.AsType());
        Assert.Equal(action, descriptor.ActionName);
        if (state != null) Assert.Equal(state, request.Request.RouteValues["state"]);
    }
}
