using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Newtonsoft.Json;
using Xunit;
// Models.OrderService (an order LINE) and Services.OrderService (the service class) share a name,
// and this file needs both namespaces, so the line type is aliased. Without this the file does not
// compile at all — which is how it shipped in Phase 1, so these tests had never been run.
using OrderLine = DreamCleaningBackend.Models.OrderService;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// The two halves of the fabricated-snapshot defect found on 2026-08-31.
    ///
    /// Several call sites used to hand <c>LogUpdateAsync</c> a hand-picked partial "before"
    /// object alongside the fully populated live entity. GetChangedFields reflects over every
    /// scalar, so each field the snapshot did not copy was recorded as a change from its CLR
    /// default - and <c>ApplyJsonValuesAsync</c> replays exactly the fields named in
    /// ChangedFields, so one Undo click would have written those zeros onto the live order.
    ///
    /// <see cref="AuditSnapshot.Of"/> stops new rows being written that way;
    /// <see cref="AuditSnapshot.HasFabricatedBeforeImage"/> stops existing ones being replayed.
    /// </summary>
    public class AuditSnapshotGuardTests
    {
        // ===== AuditSnapshot.Of =====

        [Fact]
        public void Of_CopiesEveryScalar_SoNoFieldReadsAsChangedFromZero()
        {
            var order = new Order
            {
                Id = 315,
                UserId = 42,
                Status = OrderStatuses.Done,
                ServiceDate = new DateTime(2026, 8, 14),
                OrderDate = new DateTime(2026, 8, 1),
                ServiceTime = new TimeSpan(9, 30, 0),
                SubTotal = 289.50m,
                Total = 315.20m,
                Tax = 25.70m,
                CleanerTotalSalary = 175.00m,
                CleanerHourlyRate = 25m,
                MaidsCount = 2,
                IsHidden = false,
                ContactFirstName = "Maia",
                PaymentMethod = PaymentMethod.Cash
            };

            var snapshot = AuditSnapshot.Of(order);

            Assert.Equal(order.Id, snapshot.Id);
            Assert.Equal(order.UserId, snapshot.UserId);
            Assert.Equal(order.Status, snapshot.Status);
            Assert.Equal(order.ServiceDate, snapshot.ServiceDate);
            Assert.Equal(order.OrderDate, snapshot.OrderDate);
            Assert.Equal(order.ServiceTime, snapshot.ServiceTime);
            Assert.Equal(order.SubTotal, snapshot.SubTotal);
            Assert.Equal(order.Total, snapshot.Total);
            Assert.Equal(order.Tax, snapshot.Tax);
            Assert.Equal(order.CleanerTotalSalary, snapshot.CleanerTotalSalary);
            Assert.Equal(order.CleanerHourlyRate, snapshot.CleanerHourlyRate);
            Assert.Equal(order.MaidsCount, snapshot.MaidsCount);
            Assert.Equal(order.ContactFirstName, snapshot.ContactFirstName);
            Assert.Equal(order.PaymentMethod, snapshot.PaymentMethod);
        }

        [Fact]
        public void Of_IsDetached_SoMutatingTheOriginalDoesNotMoveTheSnapshot()
        {
            var order = new Order { Id = 1, SubTotal = 100m, Status = OrderStatuses.Active };
            var snapshot = AuditSnapshot.Of(order);

            order.SubTotal = 250m;
            order.Status = OrderStatuses.Done;

            Assert.Equal(100m, snapshot.SubTotal);
            Assert.Equal(OrderStatuses.Active, snapshot.Status);
        }

        [Fact]
        public void Of_DoesNotCopyNavigationProperties_SoItHoldsNoReferenceToATrackedEntity()
        {
            var order = new Order
            {
                Id = 1,
                User = new User { Id = 7, Email = "someone@example.com" },
                OrderServices = new List<OrderLine> { new() { Id = 3, Quantity = 2 } }
            };

            var snapshot = AuditSnapshot.Of(order);

            Assert.Null(snapshot.User);
            // Order-line changes are audited separately by LogOrderServiceChanges.
            Assert.True(snapshot.OrderServices == null || snapshot.OrderServices.Count == 0);
        }

        // ===== HasFabricatedBeforeImage =====

        private static AuditLog OrderUpdateLog(object before, object after, params string[] changedFields) =>
            new()
            {
                Id = 2112,
                EntityType = "Order",
                EntityId = 315,
                Action = "Update",
                OldValues = JsonConvert.SerializeObject(before),
                NewValues = JsonConvert.SerializeObject(after),
                ChangedFields = JsonConvert.SerializeObject(changedFields)
            };

        [Fact]
        public void Blocks_TheHideUnhideShape_WhereOnlySixFieldsWereCopied()
        {
            // What the hide/unhide endpoint used to write: a six-field snapshot compared against
            // the live order, so ServiceDate / SubTotal / Total all read as "changed from nothing".
            var log = OrderUpdateLog(
                before: new { Id = 315, UserId = 42, Status = "Done", IsHidden = false, ServiceDate = default(DateTime), SubTotal = 0m, Total = 0m },
                after: new { Id = 315, UserId = 42, Status = "Done", IsHidden = true, ServiceDate = new DateTime(2026, 8, 14), SubTotal = 289.50m, Total = 315.20m },
                "IsHidden", "HiddenAt", "ServiceDate", "SubTotal", "Total", "Tax", "CleanerTotalSalary");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Blocks_TheOrderTransferShape_WhereTotalWasCopiedButServiceDateWasNot()
        {
            // OrderTransferService copied Total but not ServiceDate or SubTotal, so a guard that
            // looked only at money columns would have let this one through.
            var log = OrderUpdateLog(
                before: new { Id = 296, UserId = 42, Total = 315.20m, ServiceDate = default(DateTime), SubTotal = 0m },
                after: new { Id = 296, UserId = 55, Total = 315.20m, ServiceDate = new DateTime(2026, 8, 2), SubTotal = 289.50m },
                "UserId", "ServiceDate", "SubTotal");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_TheMinimalSnapshotShape_WhereBothSidesAreEquallySparse()
        {
            // SetCustomServiceName builds BOTH sides as minimal Order objects, so only the one
            // real field is ever listed. Undo has to keep working for these.
            var log = OrderUpdateLog(
                before: new { Id = 315, CustomServiceDisplayName = "Arranged", ServiceDate = default(DateTime) },
                after: new { Id = 315, CustomServiceDisplayName = "Deep", ServiceDate = default(DateTime) },
                "CustomServiceDisplayName");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_AGenuineServiceDateChange_WhereTheBeforeValueIsARealDate()
        {
            var log = OrderUpdateLog(
                before: new { Id = 315, ServiceDate = new DateTime(2026, 8, 14), SubTotal = 289.50m },
                after: new { Id = 315, ServiceDate = new DateTime(2026, 8, 20), SubTotal = 289.50m },
                "ServiceDate");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_AZeroDollarOrder_WhenTheSentinelIsDefaultOnBothSides()
        {
            // A $0 order legitimately carries SubTotal 0. Because the value is default on BOTH
            // sides it is a real (if odd) no-op rather than a missing field, so it is not blocked.
            var log = OrderUpdateLog(
                before: new { Id = 400, SubTotal = 0m, Status = "Active" },
                after: new { Id = 400, SubTotal = 0m, Status = "Done" },
                "SubTotal", "Status");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        // ===== User =====

        private static AuditLog Log(string entityType, object before, object after, params string[] changedFields) =>
            new()
            {
                Id = 3000,
                EntityType = entityType,
                EntityId = 42,
                Action = "Update",
                OldValues = JsonConvert.SerializeObject(before),
                NewValues = JsonConvert.SerializeObject(after),
                ChangedFields = JsonConvert.SerializeObject(changedFields)
            };

        [Fact]
        public void Blocks_TheAdminUsersControllerShape_ViaCreatedAt()
        {
            // The six AdminUsersController endpoints copied between 6 and 13 of User's 70 scalars
            // and never CreatedAt, so every row they wrote names it with a 0001-01-01 before-value.
            var log = Log("User",
                before: new { Id = 42, Email = "a@b.com", Role = 1, CreatedAt = default(DateTime), PasswordHash = (string?)null },
                after: new { Id = 42, Email = "a@b.com", Role = 2, CreatedAt = new DateTime(2026, 3, 1), PasswordHash = "hash" },
                "Role", "CreatedAt", "PasswordHash", "BubblePoints");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Blocks_TheAuthServiceEmailChangeShape_WhichCopiedCreatedAtAndNeedsPasswordHash()
        {
            // AuthService's email-change handler copied 11 of 70 scalars INCLUDING CreatedAt,
            // Email and Role - so CreatedAt alone would have let it through. PasswordHash is the
            // sentinel that catches it, and this is the entire reason User has four sentinels
            // rather than one.
            var log = Log("User",
                before: new { Id = 42, Email = "old@b.com", Role = 1, CreatedAt = new DateTime(2026, 3, 1), PasswordHash = (string?)null, BubblePoints = 0 },
                after: new { Id = 42, Email = "new@b.com", Role = 1, CreatedAt = new DateTime(2026, 3, 1), PasswordHash = "hash", BubblePoints = 340 },
                "Email", "PasswordHash", "BubblePoints");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_AnOAuthUser_WhosePasswordHashIsNullOnBothSides()
        {
            // A Google/Apple account has no password. Null on both sides is a real state, not a
            // missing field, so the both-sides-default exemption has to clear it or every OAuth
            // user's audit rows would become un-undoable.
            var log = Log("User",
                before: new { Id = 42, Email = "a@b.com", Role = 1, CreatedAt = new DateTime(2026, 3, 1), PasswordHash = (string?)null },
                after: new { Id = 42, Email = "a@b.com", Role = 1, CreatedAt = new DateTime(2026, 3, 1), PasswordHash = (string?)null },
                "PasswordHash", "Phone");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_AGenuineUserEdit_WhereNoSentinelIsNamed()
        {
            var log = Log("User",
                before: new { Id = 42, Email = "a@b.com", Role = 1, CreatedAt = new DateTime(2026, 3, 1), Phone = "2125550134" },
                after: new { Id = 42, Email = "a@b.com", Role = 1, CreatedAt = new DateTime(2026, 3, 1), Phone = "2125550199" },
                "Phone");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        // ===== Catalogue types =====

        [Fact]
        public void Blocks_TheServiceEditShape_WhichWouldNullAPopulatedZeroQuantityCost()
        {
            // AdminCatalogController's service update omitted ChargeAboveThreshold,
            // ZeroQuantityCost and ZeroQuantityDuration. Replaying those fabricated nulls over a
            // service that HAS them set breaks the bedrooms-keyed studio pricing rule - the
            // calculator's generic zero-quantity branch stops firing. CreatedAt is what catches
            // the row; the ZeroQuantity columns are deliberately NOT sentinels themselves,
            // because their null is legitimate and mandatory on the levels row.
            var log = Log("Service",
                before: new { Id = 7, Name = "Bedrooms", CreatedAt = default(DateTime), ZeroQuantityCost = (decimal?)null },
                after: new { Id = 7, Name = "Bedrooms", CreatedAt = new DateTime(2026, 1, 5), ZeroQuantityCost = (decimal?)10m },
                "CreatedAt", "ZeroQuantityCost", "ChargeAboveThreshold");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_AGenuineServiceEdit_ThatSetsZeroQuantityCostFromNull()
        {
            // The false positive we chose NOT to create. ZeroQuantityCost moves null -> 10 here
            // exactly as in the fabricated row above, but CreatedAt is real, so the row stands.
            // Had ZeroQuantityCost been a sentinel this legitimate undo would be refused.
            var log = Log("Service",
                before: new { Id = 7, Name = "Bedrooms", CreatedAt = new DateTime(2026, 1, 5), ZeroQuantityCost = (decimal?)null },
                after: new { Id = 7, Name = "Bedrooms", CreatedAt = new DateTime(2026, 1, 5), ZeroQuantityCost = (decimal?)10m },
                "ZeroQuantityCost");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Blocks_TheServiceTypeDeactivateShape()
        {
            // The activate/deactivate handlers copied 10 of 13 scalars, dropping MinimumPrice as
            // well as CreatedAt - so deactivating a service type logged its minimum price as
            // falling to zero.
            var log = Log("ServiceType",
                before: new { Id = 1, Name = "Residential", IsActive = true, MinimumPrice = 0m, CreatedAt = default(DateTime) },
                after: new { Id = 1, Name = "Residential", IsActive = false, MinimumPrice = 120m, CreatedAt = new DateTime(2026, 1, 5) },
                "IsActive", "MinimumPrice", "CreatedAt");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Blocks_TheGiftCardMarkPaidShape_WhichDroppedTheFaceValue()
        {
            // GiftCardService's mark-paid path carried only 6 of GiftCard's 16 scalars, dropping
            // OriginalAmount along with the recipient and sender details.
            var log = Log("GiftCard",
                before: new { Id = 9, Code = "AAAA-BBBB-CCCC", IsPaid = false, OriginalAmount = 0m, CreatedAt = default(DateTime) },
                after: new { Id = 9, Code = "AAAA-BBBB-CCCC", IsPaid = true, OriginalAmount = 250m, CreatedAt = new DateTime(2026, 2, 2) },
                "IsPaid", "OriginalAmount", "CreatedAt");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void Allows_AGenuineGiftCardBalanceEdit()
        {
            var log = Log("GiftCard",
                before: new { Id = 9, Code = "AAAA-BBBB-CCCC", CurrentBalance = 250m, CreatedAt = new DateTime(2026, 2, 2) },
                after: new { Id = 9, Code = "AAAA-BBBB-CCCC", CurrentBalance = 180m, CreatedAt = new DateTime(2026, 2, 2) },
                "CurrentBalance");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Theory]
        [InlineData("ExtraService")]
        [InlineData("PromoCode")]
        public void Blocks_TheRemainingCatalogueShapes_ViaCreatedAt(string entityType)
        {
            var log = Log(entityType,
                before: new { Id = 3, IsActive = true, CreatedAt = default(DateTime) },
                after: new { Id = 3, IsActive = false, CreatedAt = new DateTime(2026, 1, 5) },
                "IsActive", "CreatedAt");

            Assert.True(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        // ===== Types deliberately left unguarded =====

        [Theory]
        [InlineData("Subscription")]   // snapshot was 9/9 - never produced a fabricated row
        [InlineData("SpecialOffer")]   // snapshot was 16/16 - same
        [InlineData("Apartment")]
        [InlineData("OrderServicesUpdate")]
        public void IgnoresEntityTypesNotInTheSentinelMap(string entityType)
        {
            // Guarding a type whose snapshots were always complete could only cost false
            // refusals, so absence from the map is a decision, not an oversight.
            var log = Log(entityType,
                before: new { Id = 1, CreatedAt = default(DateTime) },
                after: new { Id = 1, CreatedAt = new DateTime(2026, 1, 5) },
                "CreatedAt");

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void OnlyUpdateRowsAreTested()
        {
            var log = Log("User",
                before: new { Id = 42, CreatedAt = default(DateTime) },
                after: new { Id = 42, CreatedAt = new DateTime(2026, 3, 1) },
                "CreatedAt");
            log.Action = "Create";

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void TheSentinelMapCoversExactlyTheTypesThatHadPartialSnapshots()
        {
            // Order and User were fixed first; the five catalogue types followed. Subscription and
            // SpecialOffer are absent on purpose. If a type is added here, a partial snapshot was
            // found somewhere - make sure the write side was fixed too, not just the guard.
            Assert.Equal(
                new[] { "ExtraService", "GiftCard", "Order", "PromoCode", "Service", "ServiceType", "User" },
                AuditSnapshot.FabricationSentinelFields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

            Assert.Equal(new[] { "CreatedAt", "PasswordHash", "Email", "Role" },
                AuditSnapshot.FabricationSentinelFields["User"]);

            // The catalogue types are CreatedAt-only by design - see the map's comment for why
            // extra sentinels would add false positives without adding coverage.
            foreach (var type in new[] { "GiftCard", "Service", "ServiceType", "ExtraService", "PromoCode" })
                Assert.Equal(new[] { "CreatedAt" }, AuditSnapshot.FabricationSentinelFields[type]);
        }

        [Fact]
        public void UnreadableJsonIsNotTreatedAsFabrication()
        {
            // Not evidence of a partial snapshot - the undo will fail on its own terms, with a
            // message about the JSON rather than a misleading one about the before-image.
            var log = new AuditLog
            {
                EntityType = "Order",
                Action = "Update",
                OldValues = "{not json",
                NewValues = "{not json",
                ChangedFields = "[\"ServiceDate\"]"
            };

            Assert.False(AuditSnapshot.HasFabricatedBeforeImage(log));
        }

        [Fact]
        public void TheRefusalMessageTellsTheAdminWhatToDoNext()
        {
            // "the RECORD", not "the order": the guard covers User, GiftCard and the catalogue
            // types as well, so the message cannot name orders specifically. What is being pinned
            // here is that the refusal names a next step at all — "this is corrupt" on its own
            // leaves the admin with nothing to do.
            Assert.Contains("Make the correction directly on the record",
                AuditSnapshot.FabricatedBeforeImageMessage);
        }
    }
}
