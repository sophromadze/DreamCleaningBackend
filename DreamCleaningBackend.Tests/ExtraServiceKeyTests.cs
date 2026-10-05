using System.Text.Json;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// EXTRAS ARE RECOGNISED BY KEY, NOT BY NAME (2026-10).
    ///
    /// Renaming an extra in Admin > Services used to change what it did: "Extra Cleaners" stopped
    /// adding cleaners, "Cleaning Supplies" stopped shortening the customer's checklist, and the FAQ
    /// lost its price. A keyed extra is now recognised by ExtraService.ExtraServiceKey whatever it is
    /// called; only an UNKEYED row falls back to the old name rules (which every older spec in this
    /// suite still exercises through the name-only overloads).
    /// </summary>
    public class ExtraServiceKeyTests
    {
        private static ExtraService Extra(string name, string? key = null, bool hasQuantity = false,
            bool deep = false, bool superDeep = false, int? serviceTypeId = null, bool universal = true, int id = 0) =>
            new()
            {
                Id = id,
                Name = name,
                ExtraServiceKey = key,
                HasQuantity = hasQuantity,
                IsDeepCleaning = deep,
                IsSuperDeepCleaning = superDeep,
                ServiceTypeId = serviceTypeId,
                IsAvailableForAll = universal,
                IsActive = true
            };

        private static Order OrderWith(params ExtraService[] extras) => new()
        {
            OrderExtraServices = extras.Select(e => new OrderExtraService { ExtraService = e }).ToList()
        };

        // ── Pricing: Extra Cleaners ────────────────────────────────────────────────────────

        [Fact]
        public void ARenamedExtraCleanersRowStillAddsCleaners()
        {
            var line = new OrderPricingCalculator.ExtraServiceLineInput
            {
                Name = "Additional Team Members", ExtraServiceKey = ExtraServiceKeys.ExtraCleaners, HasQuantity = true
            };
            Assert.True(line.IsExtraCleaners);
        }

        [Fact]
        public void AKeyedRowNamedExtraCleanersIsNotExtraCleaners()
        {
            // The key decides once it is set - the name is never consulted for a keyed row.
            var line = new OrderPricingCalculator.ExtraServiceLineInput
            {
                Name = OrderPricingCalculator.ExtraCleanersName, ExtraServiceKey = "pets", HasQuantity = true
            };
            Assert.False(line.IsExtraCleaners);
        }

        [Fact]
        public void AnUnkeyedRowFallsBackToTheExactLegacyName()
        {
            Assert.True(new OrderPricingCalculator.ExtraServiceLineInput
                { Name = "Extra Cleaners", HasQuantity = true }.IsExtraCleaners);
            Assert.False(new OrderPricingCalculator.ExtraServiceLineInput
                { Name = "Extra Cleaners (team)", HasQuantity = true }.IsExtraCleaners);
        }

        // ── Supply checklist and cleaner view ──────────────────────────────────────────────

        [Fact]
        public void RenamedSupplyExtrasStillDriveTheChecklist()
        {
            var facts = CustomerSupplyChecklist.Resolve(OrderWith(
                Extra("Eco Products Kit", ExtraServiceKeys.CleaningSupplies),
                Extra("Paper & Bags", ExtraServiceKeys.CleaningEssentials),
                Extra("Hoover", ExtraServiceKeys.VacuumCleaner),
                Extra("Range Interior", ExtraServiceKeys.Oven)));

            Assert.True(facts.HasCleaningSupplies);
            Assert.True(facts.HasCleaningEssentials);
            Assert.True(facts.WeBringVacuum);
            Assert.True(facts.RequiresOvenCleaner);
            Assert.Empty(CustomerSupplyChecklist.BuildItems(facts));
        }

        [Fact]
        public void AKeyedRowWhoseNameLooksLikeASupplyExtraDoesNothing()
        {
            var facts = CustomerSupplyChecklist.Resolve(OrderWith(
                Extra("Cleaning Supplies storage", "closets"),
                Extra("Vacuum the couch", "couches")));

            Assert.False(facts.HasCleaningSupplies);
            Assert.False(facts.WeBringVacuum);
        }

        [Fact]
        public void TheCleanerViewHidesRenamedKeyedExtras_AndShowsOrdinaryOnes()
        {
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners(Extra("Our products", ExtraServiceKeys.CleaningSupplies)));
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners(Extra("Paper goods", ExtraServiceKeys.CleaningEssentials)));
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners(Extra("More people", ExtraServiceKeys.ExtraCleaners, hasQuantity: true)));
            Assert.True(CleanerJobView.IsExtraHiddenFromCleaners(Extra("Thorough clean", ExtraServiceKeys.DeepCleaning, deep: true)));
            Assert.False(CleanerJobView.IsExtraHiddenFromCleaners(Extra("Range Interior", ExtraServiceKeys.Oven)));
        }

        [Fact]
        public void TheCleaningTypeFollowsTheDeepFlags_NotTheName()
        {
            var deep = OrderWith(Extra("Thorough clean", ExtraServiceKeys.DeepCleaning, deep: true));
            deep.ServiceType = new ServiceType { Name = "Home Care", ServiceKey = "residential" };
            Assert.Equal("Deep Cleaning", CleanerJobView.ResolveCleaningTypeName(deep));

            var superDeep = OrderWith(Extra("Top to bottom", "super-deep", superDeep: true));
            superDeep.ServiceType = new ServiceType { Name = "Home Care", ServiceKey = "residential" };
            Assert.Equal("Super Deep Cleaning", CleanerJobView.ResolveCleaningTypeName(superDeep));

            // Keyed residential type with a name the old rule would not have recognised.
            var regular = OrderWith(Extra("Range Interior", ExtraServiceKeys.Oven));
            regular.ServiceType = new ServiceType { Name = "Apartment Care", ServiceKey = "residential" };
            Assert.Equal("Regular Cleaning", CleanerJobView.ResolveCleaningTypeName(regular));

            // A keyed non-residential type keeps its own name even when it says "Home".
            var office = OrderWith();
            office.ServiceType = new ServiceType { Name = "Home Office Cleaning", ServiceKey = "office" };
            Assert.Equal("Home Office Cleaning", CleanerJobView.ResolveCleaningTypeName(office));
        }

        // ── Default cleaner hourly rate ────────────────────────────────────────────────────

        [Theory]
        [InlineData("filthy", 0, 28)]
        [InlineData("heavy-condition", 0, 25)]
        [InlineData("post-construction", 0, 25)]
        [InlineData("move-in-out", 0, 21)]
        [InlineData("residential", 1, 21)]
        [InlineData("residential", 0, 20)]
        [InlineData("office", 0, 20)]
        [InlineData("custom", 0, 20)]
        public void TheHourlyRateFollowsTheServiceTypeKey_WhateverTheTypeIsCalled(string key, int deepFee, int expected)
        {
            // A name that would have matched a DIFFERENT keyword proves the key decides.
            Assert.Equal(expected, OrderPricingCalculator.GetDefaultCleanerHourlyRate(deepFee, "Filthy Move Deep", key));
        }

        [Fact]
        public void WithoutAKeyTheHourlyRateStillReadsTheName()
        {
            Assert.Equal(28m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(0m, "Filthy Cleaning"));
            Assert.Equal(21m, OrderPricingCalculator.GetDefaultCleanerHourlyRate(0m, "Deep Cleaning", null));
        }

        [Fact]
        public void ACustomOrderIsRatedByItsLabelEvenWhenItsTypeIsKeyed()
        {
            var order = new Order
            {
                ServiceType = new ServiceType { Name = "Pre-arranged", IsCustom = true, ServiceKey = "custom" },
                CustomServiceDisplayName = "Filthy"
            };
            Assert.Null(order.GetRecognisableServiceTypeKey());
        }

        // ── Key rules ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void CopiesInDifferentTypesMayShareAKey_ButNothingAServiceTypeSeesTwice()
        {
            var residentialCopy = Extra("Extra Cleaners", "extra-cleaners", serviceTypeId: 1, universal: false);
            var universalSupplies = Extra("Cleaning Supplies", "cleaning-supplies");

            // Move In/Out's own copy beside Residential's: fine.
            Assert.False(ExtraServiceKeyPolicy.Conflicts(isAvailableForAll: false, serviceTypeId: 4, residentialCopy));
            // A second Residential row with the same key: Residential would see both.
            Assert.True(ExtraServiceKeyPolicy.Conflicts(isAvailableForAll: false, serviceTypeId: 1, residentialCopy));
            // Any type-specific row against a universal key: that type sees the universal row too.
            Assert.True(ExtraServiceKeyPolicy.Conflicts(isAvailableForAll: false, serviceTypeId: 2, universalSupplies));
            // A universal row against any holder.
            Assert.True(ExtraServiceKeyPolicy.Conflicts(isAvailableForAll: true, serviceTypeId: null, residentialCopy));
        }

        [Theory]
        [InlineData("Extra-Cleaners")]
        [InlineData("extra cleaners")]
        [InlineData("extra_cleaners")]
        [InlineData("-extra")]
        public void MalformedKeysAreRefused(string key)
        {
            var problem = ExtraServiceKeyPolicy.DescribeProblem(key);
            Assert.NotNull(problem);
            Assert.StartsWith("Extra service key", problem);
        }

        [Fact]
        public void ABlankKeyMeansNoKey()
        {
            Assert.Null(ExtraServiceKeyPolicy.Normalize("   "));
            Assert.Equal("oven", ExtraServiceKeyPolicy.Normalize(" oven "));
        }

        [Fact]
        public void AnUpdateWithoutTheFieldLeavesTheKeyAlone_AnExplicitNullClearsIt()
        {
            // An admin page loaded before the deploy posts no ExtraServiceKey at all; it must not
            // wipe the keys the migration filled in.
            var absent = JsonSerializer.Deserialize<UpdateExtraServiceDto>(
                "{\"name\":\"Oven\",\"price\":55,\"duration\":30}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.False(absent.ExtraServiceKeyProvided);

            var cleared = JsonSerializer.Deserialize<UpdateExtraServiceDto>(
                "{\"name\":\"Oven\",\"price\":55,\"duration\":30,\"extraServiceKey\":null}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.True(cleared.ExtraServiceKeyProvided);
            Assert.Null(cleared.ExtraServiceKey);
        }

        [Fact]
        public void APreArrangedTypeDeDuplicatesCopiesByKey_EvenWhenOneWasRenamed()
        {
            var catalogue = new List<ExtraService>
            {
                Extra("Extra Cleaners", "extra-cleaners", serviceTypeId: 1, universal: false, id: 15),
                Extra("Additional Cleaners", "extra-cleaners", serviceTypeId: 4, universal: false, id: 34),
                Extra("Pets", null, serviceTypeId: 1, universal: false, id: 17),
                Extra("Pets", null, serviceTypeId: 4, universal: false, id: 36)
            };
            var selectable = CatalogDtoMapper.ResolveSelectableExtraServices(
                new ServiceType { Id = 7, IsCustom = true }, catalogue);

            Assert.Equal(new[] { 15, 17 }, selectable.Select(e => e.Id).OrderBy(i => i).ToArray());
        }
    }
}
