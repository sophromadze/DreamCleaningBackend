using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// UNDOING AN ORDER EDIT FROM THE AUDITS TAB TAKES ITS UPDATE-HISTORY ROW WITH IT.
    ///
    /// Order #386 (2026-09): a +$270.00 tip was undone from Audits, the order went back to
    /// $1,480.00, and the Update History panel kept an "Unpaid +$270.00" row for a price the order
    /// no longer had. The audit row and the history row have no foreign key between them, so
    /// <see cref="OrderEditHistoryLink"/> matches on the edit's fingerprint; these tests pin that
    /// match down against the pure arithmetic, no database.
    /// </summary>
    public class OrderEditHistoryLinkTests
    {
        private static readonly DateTime AuditAt = new(2026, 9, 25, 21, 41, 5, DateTimeKind.Utc);

        private static OrderUpdateHistory Row(int id, decimal original, decimal updated, int secondsBeforeAudit,
            decimal additional = 0m, bool isPaid = false, int orderId = 386) =>
            new()
            {
                Id = id,
                OrderId = orderId,
                UpdatedAt = AuditAt.AddSeconds(-secondsBeforeAudit),
                OriginalTotal = original,
                NewTotal = updated,
                AdditionalAmount = additional,
                IsPaid = isPaid
            };

        [Fact]
        public void Order386_TheTipEditsRowIsTheOneFound()
        {
            var rows = new[]
            {
                Row(1, 1150m, 1480m, secondsBeforeAudit: 12_780, additional: 330m, isPaid: true), // earlier card-paid edit
                Row(2, 1480m, 1750m, secondsBeforeAudit: 1, additional: 270m)
            };

            var found = OrderEditHistoryLink.Find(rows, 386, AuditAt, totalBefore: 1480m, totalAfter: 1750m);

            Assert.Equal(2, found?.Id);
            Assert.False(OrderEditHistoryLink.HasCollectedMoney(found!));
        }

        [Fact]
        public void ARowFromAnotherOrder_OrOutsideTheWindow_OrWithOtherTotals_IsNeverMatched()
        {
            var rows = new[]
            {
                Row(1, 1480m, 1750m, secondsBeforeAudit: 1, orderId: 999),
                Row(2, 1480m, 1750m, secondsBeforeAudit: (int)OrderEditHistoryLink.WindowBefore.TotalSeconds + 60),
                Row(3, 1480m, 1760m, secondsBeforeAudit: 1)
            };

            Assert.Null(OrderEditHistoryLink.Find(rows, 386, AuditAt, 1480m, 1750m));
        }

        /// <summary>Two same-priced edits minutes apart (e.g. two duration-only saves): only the
        /// one nearest the audit row goes, never both.</summary>
        [Fact]
        public void TwoIdenticalRows_TheNearestInTimeWins()
        {
            var rows = new[]
            {
                Row(1, 1480m, 1480m, secondsBeforeAudit: 200),
                Row(2, 1480m, 1480m, secondsBeforeAudit: 2)
            };

            Assert.Equal(2, OrderEditHistoryLink.Find(rows, 386, AuditAt, 1480m, 1480m)?.Id);
        }

        /// <summary>A top-up the customer actually PAID blocks the undo; a row stamped paid only
        /// because it owed nothing does not.</summary>
        [Fact]
        public void OnlyRealMoneyBlocksTheUndo()
        {
            Assert.True(OrderEditHistoryLink.HasCollectedMoney(Row(1, 1150m, 1480m, 0, additional: 330m, isPaid: true)));
            Assert.False(OrderEditHistoryLink.HasCollectedMoney(Row(2, 1480m, 1200m, 0, additional: 0m, isPaid: true)));
            Assert.False(OrderEditHistoryLink.HasCollectedMoney(Row(3, 1480m, 1750m, 0, additional: 270m, isPaid: false)));
        }

        // ── Through AuditService.UndoAsync / RedoAsync ───────────────────────────────────────

        private static AuditLog TipEditAuditRow() => new()
        {
            EntityType = "Order",
            EntityId = 386,
            Action = "Update",
            OldValues = "{\"Id\":386,\"SubTotal\":1359.36,\"Tax\":120.64,\"Tips\":0.0,\"Total\":1480.00,\"Status\":\"Active\"}",
            NewValues = "{\"Id\":386,\"SubTotal\":1359.36,\"Tax\":120.64,\"Tips\":270.0,\"Total\":1750.00,\"Status\":\"Pending\"}",
            ChangedFields = "[\"Tips\",\"Total\",\"Status\"]",
            UserId = 7,
            CreatedAt = AuditAt
        };

        [Fact]
        public async Task UndoingTheTipEdit_RemovesItsUnpaidHistoryRow_AndRedoPutsItBack()
        {
            using var db = RecurringDiscountRegressionTests.Db();
            var audit = new DreamCleaningBackend.Services.AuditService(db,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DreamCleaningBackend.Services.AuditService>.Instance);

            db.Orders.Add(new Order { Id = 386, UserId = 1, SubTotal = 1359.36m, Tax = 120.64m, Tips = 270m, Total = 1750m,
                Status = "Pending", IsPaid = true, InitialTotal = 1150m });
            db.OrderUpdateHistories.Add(Row(1, 1150m, 1480m, secondsBeforeAudit: 12_780, additional: 330m, isPaid: true));
            db.OrderUpdateHistories.Add(Row(2, 1480m, 1750m, secondsBeforeAudit: 1, additional: 270m));
            var log = TipEditAuditRow();
            db.AuditLogs.Add(log);
            await db.SaveChangesAsync();

            await audit.UndoAsync(log.Id);

            var order = (await db.Orders.FindAsync(386))!;
            Assert.Equal(1480m, order.Total);
            Assert.Equal(0m, order.Tips);
            Assert.Equal(new[] { 1 }, db.OrderUpdateHistories.Select(h => h.Id).ToArray());

            await audit.RedoAsync(log.Id);

            Assert.Equal(1750m, order.Total);
            var restored = db.OrderUpdateHistories.Single(h => h.NewTotal == 1750m);
            Assert.Equal(270m, restored.AdditionalAmount);
            Assert.False(restored.IsPaid);
            Assert.Equal(2, db.OrderUpdateHistories.Count());
        }

        [Fact]
        public async Task AnEditWhoseTopUpWasAlreadyPaid_IsNotUndone()
        {
            using var db = RecurringDiscountRegressionTests.Db();
            var audit = new DreamCleaningBackend.Services.AuditService(db,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DreamCleaningBackend.Services.AuditService>.Instance);

            db.Orders.Add(new Order { Id = 386, UserId = 1, Tips = 270m, Total = 1750m, Status = "Active", IsPaid = true });
            db.OrderUpdateHistories.Add(Row(2, 1480m, 1750m, secondsBeforeAudit: 1, additional: 270m, isPaid: true));
            var log = TipEditAuditRow();
            db.AuditLogs.Add(log);
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => audit.UndoAsync(log.Id));

            Assert.Equal(1750m, (await db.Orders.FindAsync(386))!.Total);
            Assert.Single(db.OrderUpdateHistories);
            Assert.Null(log.UndoneAt);
        }

        /// <summary>Redo re-creates the row in exactly the shape the admin save writes.</summary>
        [Fact]
        public void Rebuild_MatchesWhatTheAdminSaveWrites()
        {
            var before = new Order { Id = 386, SubTotal = 1359.36m, Tax = 120.64m, Tips = 0m, Total = 1480m };
            var after = new Order { Id = 386, SubTotal = 1359.36m, Tax = 120.64m, Tips = 270m, Total = 1750m };

            var row = OrderEditHistoryLink.Rebuild(before, after, updatedByUserId: 7, editedAt: AuditAt);

            Assert.Equal(270m, row.AdditionalAmount);
            Assert.False(row.IsPaid);
            Assert.Equal(1480m, row.OriginalTotal);
            Assert.Equal(1750m, row.NewTotal);
            Assert.Equal(270m, row.NewTips);
            Assert.Equal(AuditAt, row.UpdatedAt);
            Assert.Same(row, OrderEditHistoryLink.Find(new[] { row }, 386, AuditAt, 1480m, 1750m));

            var decrease = OrderEditHistoryLink.Rebuild(after, before, 7, AuditAt);
            Assert.Equal(0m, decrease.AdditionalAmount);
            Assert.True(decrease.IsPaid);
        }
    }
}
