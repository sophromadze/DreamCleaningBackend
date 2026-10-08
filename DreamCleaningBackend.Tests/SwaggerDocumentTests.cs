using DreamCleaningBackend.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// /swagger MUST DESCRIBE EVERY ACTION (2026-10, Swashbuckle 6.6 -> 10.3).
    ///
    /// Swashbuckle builds the document from every controller at once, so one action it cannot
    /// describe takes the whole of /swagger/v1/swagger.json down with a 500. Swashbuckle 10 started
    /// refusing [FromForm] on an IFormFile parameter, which is exactly how the before/after photo
    /// upload was declared. This generates the document over all of the app's controllers.
    /// </summary>
    public class SwaggerDocumentTests
    {
        [Fact]
        public async Task TheDocumentDescribesEveryControllerAction()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new OpenApiInfo { Title = "API", Version = "v1" }));
            await using var app = builder.Build();
            app.MapControllers();

            var document = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

            Assert.True(document.Paths.Count > 100, $"Only {document.Paths.Count} paths were described.");

            var upload = document.Paths["/api/admin/before-after-photos"].Operations![HttpMethod.Post];
            var form = upload.RequestBody!.Content!["multipart/form-data"].Schema!;
            Assert.Contains("beforeFile", form.Properties!.Keys);
            Assert.Contains("afterFile", form.Properties!.Keys);
            Assert.Contains("title", form.Properties!.Keys);
        }
    }
}
