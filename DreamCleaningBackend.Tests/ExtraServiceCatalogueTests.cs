using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHICH EXTRAS DOES A SERVICE TYPE OFFER?
    ///
    /// Two different answers, and conflating them is the bug to watch for:
    ///
    ///   ResolveConfiguredExtraServices — the type's real configuration (own rows + universal
    ///   rows). The admin Booking Services screen lists exactly this and offers Edit/Delete per
    ///   row, so it must never be widened; a custom type showing the whole catalogue there would
    ///   invite an admin to hard-delete a shared extra while thinking they were detaching it.
    ///
    ///   ResolveSelectableExtraServices — what an order can record. Identical for ordinary types;
    ///   a CUSTOM ("Pre-Arranged") type gets the entire catalogue, because its extras are priced
    ///   at $0 / 0 min and exist only to tell admins and cleaners what the job involves.
    ///
    /// The de-duplication matters because the admin "copy to service type" action clones an extra
    /// into a new row with the same Name and IsAvailableForAll = false. A catalogue with the same
    /// extra copied onto four types would otherwise render four identical cards.
    /// </summary>
    public class ExtraServiceCatalogueTests
    {
        private const int ResidentialTypeId = 1;
        private const int OfficeTypeId = 2;
        private const int CustomTypeId = 3;

        private static ServiceType Type(int id, bool isCustom = false) =>
            new() { Id = id, Name = $"Type {id}", IsCustom = isCustom };

        private static ExtraService Extra(
            int id, string name, int? serviceTypeId, bool isAvailableForAll, int displayOrder) =>
            new()
            {
                Id = id,
                Name = name,
                ServiceTypeId = serviceTypeId,
                IsAvailableForAll = isAvailableForAll,
                DisplayOrder = displayOrder,
                IsActive = true
            };

        /// <summary>
        /// A catalogue shaped like the real one: two universal extras, one Residential-only extra,
        /// and "Oven Cleaning" existing three times — the universal row plus a per-type copy on
        /// Residential and on Office.
        /// </summary>
        private static List<ExtraService> Catalogue() => new()
        {
            Extra(1, "Cleaning Supplies", serviceTypeId: null, isAvailableForAll: true, displayOrder: 1),
            Extra(2, "Oven Cleaning", serviceTypeId: null, isAvailableForAll: true, displayOrder: 2),
            Extra(3, "Windows", ResidentialTypeId, isAvailableForAll: false, displayOrder: 3),
            Extra(4, "Oven Cleaning", ResidentialTypeId, isAvailableForAll: false, displayOrder: 2),
            Extra(5, "Oven Cleaning", OfficeTypeId, isAvailableForAll: false, displayOrder: 2),
            Extra(6, "Desk Wipe-Down", OfficeTypeId, isAvailableForAll: false, displayOrder: 4)
        };

        [Fact]
        public void Configured_OrdinaryType_ReturnsOwnRowsPlusUniversalOnes()
        {
            var result = CatalogDtoMapper.ResolveConfiguredExtraServices(Type(ResidentialTypeId), Catalogue());

            Assert.Equal(new[] { 1, 4, 2, 3 }, result.Select(es => es.Id).ToArray());
            // Nothing from another service type leaked in.
            Assert.DoesNotContain(result, es => es.ServiceTypeId == OfficeTypeId);
        }

        [Fact]
        public void Configured_CustomType_IsNotWidened_SoTheCatalogueEditorStaysSafe()
        {
            var result = CatalogDtoMapper.ResolveConfiguredExtraServices(Type(CustomTypeId, isCustom: true), Catalogue());

            // Only the universal rows — the custom type owns nothing of its own here.
            Assert.Equal(new[] { 1, 2 }, result.Select(es => es.Id).ToArray());
        }

        [Fact]
        public void Selectable_OrdinaryType_MatchesConfigured()
        {
            var type = Type(ResidentialTypeId);

            Assert.Equal(
                CatalogDtoMapper.ResolveConfiguredExtraServices(type, Catalogue()).Select(es => es.Id),
                CatalogDtoMapper.ResolveSelectableExtraServices(type, Catalogue()).Select(es => es.Id));
        }

        [Fact]
        public void Selectable_CustomType_ReturnsTheWholeCatalogue_WithoutDuplicateNames()
        {
            var result = CatalogDtoMapper.ResolveSelectableExtraServices(Type(CustomTypeId, isCustom: true), Catalogue());

            // Every distinct extra is offered, including ones that only exist on another type.
            Assert.Equal(
                new[] { "Cleaning Supplies", "Desk Wipe-Down", "Oven Cleaning", "Windows" },
                result.Select(es => es.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

            // "Oven Cleaning" appears once, and it is the UNIVERSAL row (id 2), not a per-type copy.
            var ovens = result.Where(es => es.Name == "Oven Cleaning").ToList();
            Assert.Single(ovens);
            Assert.Equal(2, ovens[0].Id);
        }

        [Fact]
        public void Selectable_CustomType_DeDuplicatesRegardlessOfCasingOrPadding()
        {
            var catalogue = Catalogue();
            catalogue.Add(Extra(7, "  oven cleaning ", OfficeTypeId, isAvailableForAll: false, displayOrder: 9));

            var result = CatalogDtoMapper.ResolveSelectableExtraServices(Type(CustomTypeId, isCustom: true), catalogue);

            Assert.Single(result, es => es.Name.Trim().Equals("Oven Cleaning", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Selectable_CustomType_IsOrderedByDisplayOrder()
        {
            var result = CatalogDtoMapper.ResolveSelectableExtraServices(Type(CustomTypeId, isCustom: true), Catalogue());

            Assert.Equal(
                result.Select(es => es.DisplayOrder).OrderBy(d => d).ToArray(),
                result.Select(es => es.DisplayOrder).ToArray());
        }
    }
}
