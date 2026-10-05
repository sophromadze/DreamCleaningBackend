using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderDiscountRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DiscountFixedAmount",
                table: "Orders",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPercent",
                table: "Orders",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SubscriptionDiscountPercent",
                table: "Orders",
                type: "decimal(5,2)",
                nullable: true);

            // Backfill (owner's decision 2026-10-05): record the rule behind an EXISTING order's
            // discount only when its stored amount is EXACTLY what that rule gives on the order's
            // own subtotal. A mismatch means the order was edited, or the promo / offer / plan
            // changed since it was booked, so the rule is not trusted and the order keeps the old
            // proportional re-scale (columns stay NULL). Exact-to-the-cent on purpose: a rule that
            // is a cent off would move the discount the first time anybody saves the order.
            // Dev-DB dry run 2026-10-05: 7/7 granted offers, 12/17 promo codes, 26/26 plans matched.

            // 1. Special offers granted to the customer (the link survives offer renames).
            migrationBuilder.Sql(@"
UPDATE Orders o
JOIN UserSpecialOffers uso ON uso.UsedOnOrderId = o.Id
JOIN SpecialOffers src ON src.Id = uso.SpecialOfferId
SET o.DiscountPercent = CASE WHEN src.IsPercentage = 1 THEN src.DiscountValue END,
    o.DiscountFixedAmount = CASE WHEN src.IsPercentage = 0 THEN src.DiscountValue END
WHERE o.DiscountAmount > 0
  AND o.DiscountPercent IS NULL AND o.DiscountFixedAmount IS NULL
  
  AND ((src.IsPercentage = 1 AND ROUND(o.SubTotal * src.DiscountValue / 100, 2) = o.DiscountAmount)
    OR (src.IsPercentage = 0 AND (o.DiscountAmount = src.DiscountValue OR o.DiscountAmount = LEAST(src.DiscountValue, GREATEST(0, o.SubTotal - o.SubscriptionDiscountAmount - o.LoyaltyDiscountAmount)))));");

            // 2. Public special offers, recorded by name only - and only where the name is unique.
            migrationBuilder.Sql(@"
UPDATE Orders o
JOIN SpecialOffers src ON src.Name = SUBSTRING(o.PromoCode, 15)
SET o.DiscountPercent = CASE WHEN src.IsPercentage = 1 THEN src.DiscountValue END,
    o.DiscountFixedAmount = CASE WHEN src.IsPercentage = 0 THEN src.DiscountValue END
WHERE o.DiscountAmount > 0
  AND o.DiscountPercent IS NULL AND o.DiscountFixedAmount IS NULL
  AND o.PromoCode LIKE 'SPECIAL_OFFER:%'
  AND (SELECT COUNT(*) FROM SpecialOffers s2 WHERE s2.Name = src.Name) = 1
  AND ((src.IsPercentage = 1 AND ROUND(o.SubTotal * src.DiscountValue / 100, 2) = o.DiscountAmount)
    OR (src.IsPercentage = 0 AND (o.DiscountAmount = src.DiscountValue OR o.DiscountAmount = LEAST(src.DiscountValue, GREATEST(0, o.SubTotal - o.SubscriptionDiscountAmount - o.LoyaltyDiscountAmount)))));");

            // 3. Promo codes (gift-card codes never match a PromoCodes row).
            migrationBuilder.Sql(@"
UPDATE Orders o
JOIN PromoCodes src ON LOWER(src.Code) = LOWER(o.PromoCode)
SET o.DiscountPercent = CASE WHEN src.IsPercentage = 1 THEN src.DiscountValue END,
    o.DiscountFixedAmount = CASE WHEN src.IsPercentage = 0 THEN src.DiscountValue END
WHERE o.DiscountAmount > 0
  AND o.DiscountPercent IS NULL AND o.DiscountFixedAmount IS NULL
  AND o.PromoCode NOT LIKE 'SPECIAL_OFFER:%'
  AND ((src.IsPercentage = 1 AND ROUND(o.SubTotal * src.DiscountValue / 100, 2) = o.DiscountAmount)
    OR (src.IsPercentage = 0 AND (o.DiscountAmount = src.DiscountValue OR o.DiscountAmount = LEAST(src.DiscountValue, GREATEST(0, o.SubTotal - o.SubscriptionDiscountAmount - o.LoyaltyDiscountAmount)))));");

            // 4. Recurring-plan discounts.
            migrationBuilder.Sql(@"
UPDATE Orders o
JOIN Subscriptions s ON s.Id = o.SubscriptionId
SET o.SubscriptionDiscountPercent = s.DiscountPercentage
WHERE o.SubscriptionDiscountAmount > 0 AND s.DiscountPercentage > 0
  AND ROUND(o.SubTotal * s.DiscountPercentage / 100, 2) = o.SubscriptionDiscountAmount;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscountFixedAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DiscountPercent",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SubscriptionDiscountPercent",
                table: "Orders");
        }
    }
}
