using System.Text.Json;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
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
    /// The Admin > Services > Extra Services key field, end to end through AdminCatalogController:
    /// format rules, "no service type sees one key twice", copies sharing a key, and the
    /// absent-field-keeps-the-key rule that protects the migration's keys from an old admin page.
    /// </summary>
    public class ExtraServiceKeyAdminTests : IDisposable
    {
        private const int Residential = 1, MoveInOut = 4;
        private readonly ApplicationDbContext _context;
        private readonly AdminCatalogController _controller;

        public ExtraServiceKeyAdminTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"extra-key-{Guid.NewGuid()}")
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

        private static CreateExtraServiceDto New(string name, string? key, int? serviceTypeId = null) => new()
        {
            Name = name, Price = 45, Duration = 30, PriceMultiplier = 1,
            ServiceTypeId = serviceTypeId, IsAvailableForAll = serviceTypeId == null, ExtraServiceKey = key
        };

        private static string? Message(IActionResult? result) =>
            result is BadRequestObjectResult bad
                ? JsonSerializer.SerializeToElement(bad.Value).GetProperty("message").GetString()
                : null;

        private async Task<ExtraServiceDto> CreateOk(CreateExtraServiceDto dto)
        {
            var result = await _controller.CreateExtraService(dto);
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            return Assert.IsType<ExtraServiceDto>(ok.Value);
        }

        private UpdateExtraServiceDto Update(ExtraService e, string name, bool sendKey = false, string? key = null)
        {
            var dto = new UpdateExtraServiceDto
            {
                Name = name, Price = e.Price, Duration = e.Duration, PriceMultiplier = e.PriceMultiplier,
                ServiceTypeId = e.ServiceTypeId, IsAvailableForAll = e.IsAvailableForAll, DisplayOrder = e.DisplayOrder
            };
            if (sendKey) dto.ExtraServiceKey = key;
            return dto;
        }

        [Fact]
        public async Task AMalformedKeyIsRefusedWithTheReason()
        {
            var result = await _controller.CreateExtraService(New("Extra Cleaners", "Extra-Cleaners", Residential));
            Assert.Contains("lowercase", Message(result.Result));
            Assert.Empty(_context.ExtraServices);
        }

        [Fact]
        public async Task CopiesInDifferentTypesShareAKey_ButOneTypeCannotHoldItTwice()
        {
            await CreateOk(New("Extra Cleaners", "extra-cleaners", Residential));
            await CreateOk(New("Extra Cleaners", "extra-cleaners", MoveInOut));

            var again = await _controller.CreateExtraService(New("More Cleaners", "extra-cleaners", Residential));
            Assert.Contains("already used by \"Extra Cleaners\"", Message(again.Result));
        }

        [Fact]
        public async Task AUniversalKeyCannotRepeatAnywhere()
        {
            await CreateOk(New("Cleaning Supplies", "cleaning-supplies"));
            var copy = await _controller.CreateExtraService(New("Supplies (office)", "cleaning-supplies", Residential));
            Assert.NotNull(Message(copy.Result));
            var universal = await _controller.CreateExtraService(New("Supplies 2", "cleaning-supplies"));
            Assert.NotNull(Message(universal.Result));
        }

        [Fact]
        public async Task ABlankKeyIsStoredAsNoKey()
        {
            var created = await CreateOk(New("Pets", "   ", Residential));
            Assert.Null(created.ExtraServiceKey);
            Assert.Null((await _context.ExtraServices.SingleAsync()).ExtraServiceKey);
        }

        [Fact]
        public async Task RenamingKeepsTheKey_WhenTheFormDoesNotSendIt()
        {
            var created = await CreateOk(New("Vacuum Cleaner", "vacuum-cleaner"));
            var row = await _context.ExtraServices.SingleAsync(e => e.Id == created.Id);

            var result = await _controller.UpdateExtraService(row.Id, Update(row, "We Bring a Hoover"));
            Assert.IsType<OkObjectResult>(result.Result);

            var saved = await _context.ExtraServices.SingleAsync(e => e.Id == created.Id);
            Assert.Equal("We Bring a Hoover", saved.Name);
            Assert.Equal("vacuum-cleaner", saved.ExtraServiceKey);
        }

        [Fact]
        public async Task TheFormCanSetChangeAndClearTheKey()
        {
            var created = await CreateOk(New("Oven", null, Residential));
            var row = await _context.ExtraServices.SingleAsync(e => e.Id == created.Id);

            Assert.IsType<OkObjectResult>((await _controller.UpdateExtraService(row.Id, Update(row, "Oven", true, "oven"))).Result);
            Assert.Equal("oven", (await _context.ExtraServices.SingleAsync()).ExtraServiceKey);

            var bad = await _controller.UpdateExtraService(row.Id, Update(row, "Oven", true, "Oven Inside"));
            Assert.NotNull(Message(bad.Result));
            Assert.Equal("oven", (await _context.ExtraServices.SingleAsync()).ExtraServiceKey);

            Assert.IsType<OkObjectResult>((await _controller.UpdateExtraService(row.Id, Update(row, "Oven", true, ""))).Result);
            Assert.Null((await _context.ExtraServices.SingleAsync()).ExtraServiceKey);
        }

        [Fact]
        public async Task ACopyKeepsTheKey_UnlessTheTargetTypeAlreadySeesIt()
        {
            var residentialCopy = await CreateOk(New("Extra Cleaners", "extra-cleaners", Residential));
            var copied = await _controller.CopyExtraService(new CopyExtraServiceDto
                { SourceExtraServiceId = residentialCopy.Id, TargetServiceTypeId = MoveInOut });
            Assert.Equal("extra-cleaners", Assert.IsType<ExtraServiceDto>(Assert.IsType<OkObjectResult>(copied.Result).Value).ExtraServiceKey);

            // Residential can already see a row keyed "extra-cleaners" (the original), so a second
            // copy into Residential is created unkeyed rather than refused.
            var duplicate = await _controller.CopyExtraService(new CopyExtraServiceDto
                { SourceExtraServiceId = residentialCopy.Id, TargetServiceTypeId = Residential });
            Assert.Null(Assert.IsType<ExtraServiceDto>(Assert.IsType<OkObjectResult>(duplicate.Result).Value).ExtraServiceKey);
        }

        [Fact]
        public async Task TheListReturnsTheKey()
        {
            await CreateOk(New("Cleaning Supplies", "cleaning-supplies"));
            var list = Assert.IsType<List<ExtraServiceDto>>(Assert.IsType<OkObjectResult>((await _controller.GetExtraServices()).Result).Value);
            Assert.Equal("cleaning-supplies", Assert.Single(list).ExtraServiceKey);
        }
    }
}
