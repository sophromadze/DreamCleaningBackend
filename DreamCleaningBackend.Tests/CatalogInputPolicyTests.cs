using System.Text.Json;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Admin > Services save rules (2026-10): one consistent customer control per service, sane
    /// ranges, one key per service type, exclusive extra roles / pricing modes, and the deep
    /// multipliers (incl. the absent-TimeMultiplier-keeps-the-stored-value rule for an admin page
    /// loaded before the field existed).
    /// </summary>
    public class CatalogInputPolicyTests : IDisposable
    {
        private const int Residential = 1;
        private readonly ApplicationDbContext _context;
        private readonly AdminCatalogController _controller;

        public CatalogInputPolicyTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"catalog-input-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            _context = new ApplicationDbContext(options);
            var audit = new AuditService(_context, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
            _controller = new AdminCatalogController(_context, audit, NullLogger<AdminCatalogController>.Instance, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public void Dispose() => _context.Dispose();

        private static string? Message(IActionResult? result) =>
            result is BadRequestObjectResult bad
                ? JsonSerializer.SerializeToElement(bad.Value).GetProperty("message").GetString()
                : null;

        private static CreateServiceDto NewService(string name, string key, string inputType = "dropdown", bool range = false) => new()
        {
            Name = name, ServiceKey = key, Cost = 25, TimeDuration = 30, ServiceTypeId = Residential,
            InputType = inputType, IsRangeInput = range, MinValue = 0, MaxValue = 6, StepValue = 1,
            ServiceRelationType = ""
        };

        private static CreateExtraServiceDto NewExtra(string name) => new()
        {
            Name = name, Price = 50, Duration = 60, PriceMultiplier = 1, TimeMultiplier = 1,
            IsAvailableForAll = true
        };

        // ── Pure rules ───────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("dropdown", false, "dropdown", false)]
        [InlineData("slider", false, "slider", true)]    // "slider" without the flag used to draw nothing
        [InlineData("dropdown", true, "slider", true)]   // "dropdown" with the flag used to draw both
        [InlineData("number", false, "dropdown", false)] // never implemented: drew nothing
        [InlineData(null, false, "dropdown", false)]
        public void NormalizeInputType_StoresOneConsistentControl(string? inputType, bool range, string expectedType, bool expectedRange)
        {
            var (type, isRange) = CatalogInputPolicy.NormalizeInputType(inputType, range);
            Assert.Equal(expectedType, type);
            Assert.Equal(expectedRange, isRange);
        }

        [Fact]
        public void ARegularRole_IsStoredAsNull()
        {
            Assert.Null(CatalogInputPolicy.NormalizeRelationType(""));
            Assert.Null(CatalogInputPolicy.NormalizeRelationType("  "));
            Assert.Equal("cleaner", CatalogInputPolicy.NormalizeRelationType("Cleaner"));
        }

        [Fact]
        public void NonDeepExtras_StoreNeutralMultipliers()
        {
            Assert.Equal((1m, 1m), CatalogInputPolicy.NormalizeMultipliers(false, false, 1.5m, 1.2m));
            Assert.Equal((1.5m, 1.2m), CatalogInputPolicy.NormalizeMultipliers(true, false, 1.5m, 1.2m));
        }

        // ── Services through the controller ──────────────────────────────────────────────

        [Fact]
        public async Task TheSliderIsOnlyForSqft_AndIsStoredConsistently()
        {
            var refused = await _controller.CreateService(NewService("Bedrooms", "bedrooms", "slider"));
            Assert.Contains("only available for the Sq.ft service", Message(refused.Result));

            var ok = await _controller.CreateService(NewService("Square Feet", "sqft", "dropdown", range: true));
            Assert.IsType<OkObjectResult>(ok.Result);
            var saved = await _context.Services.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("slider", saved.InputType);
            Assert.True(saved.IsRangeInput);
        }

        [Fact]
        public async Task OneKeyPerServiceType()
        {
            Assert.IsType<OkObjectResult>((await _controller.CreateService(NewService("Bedrooms", "bedrooms"))).Result);
            var again = await _controller.CreateService(NewService("Bedrooms 2", "bedrooms"));
            Assert.Contains("already has a service with the key \"bedrooms\"", Message(again.Result));
        }

        [Fact]
        public async Task MinimumAboveMaximum_IsRefused()
        {
            var dto = NewService("Bathrooms", "bathrooms");
            dto.MinValue = 5;
            dto.MaxValue = 2;
            Assert.Contains("cannot be greater than maximum", Message((await _controller.CreateService(dto)).Result));
        }

        [Fact]
        public async Task ABlankRole_IsSavedAsARegularService()
        {
            Assert.IsType<OkObjectResult>((await _controller.CreateService(NewService("Bedrooms", "bedrooms"))).Result);
            Assert.Null((await _context.Services.SingleAsync(TestContext.Current.CancellationToken)).ServiceRelationType);
        }

        // ── Extras through the controller ────────────────────────────────────────────────

        [Fact]
        public async Task DeepAndSuperDeep_CannotBothBeTicked()
        {
            var dto = NewExtra("Deep");
            dto.IsDeepCleaning = true;
            dto.IsSuperDeepCleaning = true;
            Assert.Contains("not both", Message((await _controller.CreateExtraService(dto)).Result));
        }

        [Fact]
        public async Task QuantityAndHours_CannotBothBeTicked()
        {
            var dto = NewExtra("Windows");
            dto.HasQuantity = true;
            dto.HasHours = true;
            Assert.Contains("not both", Message((await _controller.CreateExtraService(dto)).Result));
        }

        [Fact]
        public async Task ADeepExtra_SavesBothMultipliers_AndRefusesZero()
        {
            var bad = NewExtra("Deep Cleaning");
            bad.IsDeepCleaning = true;
            bad.PriceMultiplier = 1.5m;
            bad.TimeMultiplier = 0m;
            Assert.Contains("Time multiplier", Message((await _controller.CreateExtraService(bad)).Result));

            var good = NewExtra("Deep Cleaning");
            good.IsDeepCleaning = true;
            good.PriceMultiplier = 1.5m;
            good.TimeMultiplier = 1.25m;
            var ok = Assert.IsType<OkObjectResult>((await _controller.CreateExtraService(good)).Result);
            var dto = Assert.IsType<ExtraServiceDto>(ok.Value);
            Assert.Equal(1.5m, dto.PriceMultiplier);
            Assert.Equal(1.25m, dto.TimeMultiplier);
        }

        [Fact]
        public async Task AnUpdateWithoutTheTimeMultiplier_KeepsTheStoredOne()
        {
            var created = NewExtra("Deep Cleaning");
            created.IsDeepCleaning = true;
            created.PriceMultiplier = 1.5m;
            created.TimeMultiplier = 1.25m;
            var id = Assert.IsType<ExtraServiceDto>(Assert.IsType<OkObjectResult>(
                (await _controller.CreateExtraService(created)).Result).Value).Id;

            // An admin page from before the field existed sends no TimeMultiplier at all.
            var update = new UpdateExtraServiceDto
            {
                Name = "Deep Cleaning", Price = 90, Duration = 120, IsDeepCleaning = true,
                PriceMultiplier = 1.5m, IsAvailableForAll = true, DisplayOrder = 1
            };
            Assert.IsType<OkObjectResult>((await _controller.UpdateExtraService(id, update)).Result);

            var saved = await _context.ExtraServices.SingleAsync(e => e.Id == id, TestContext.Current.CancellationToken);
            Assert.Equal(1.25m, saved.TimeMultiplier);
            Assert.Equal(90m, saved.Price);
        }
    }
}
