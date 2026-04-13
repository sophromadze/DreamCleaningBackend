using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddBubbleRewardsSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BubbleCredits",
                table: "Users",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "BubblePoints",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveOrderCount",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCompletedOrderDate",
                table: "Users",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "Users",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ReferredByUserId",
                table: "Users",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewBonusGranted",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalSpentAmount",
                table: "Users",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "WelcomeBonusGranted",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BubblePointsHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BubblePointsHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BubblePointsHistories_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BubblePointsHistories_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BubbleRewardsSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SettingKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SettingValue = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Category = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, defaultValue: "General")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BubbleRewardsSettings", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Referrals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ReferrerUserId = table.Column<int>(type: "int", nullable: false),
                    ReferredUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RegistrationBonusGiven = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OrderBonusGiven = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Referrals_Users_ReferredUserId",
                        column: x => x.ReferredUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Referrals_Users_ReferrerUserId",
                        column: x => x.ReferrerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "BubbleRewardsSettings",
                columns: new[] { "Id", "Category", "Description", "SettingKey", "SettingValue", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "Points", "How many bubble points per $1 spent", "PointsPerDollar", "2", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, "Points", "Master on/off switch for entire rewards system", "PointsSystemEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 3, "Tiers", "Max total spent for Bubble tier", "TierBubbleMaxSpent", "499", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, "Tiers", "Min total spent for Super Bubble tier", "TierSuperBubbleMinSpent", "500", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 5, "Tiers", "Max total spent for Super Bubble tier", "TierSuperBubbleMaxSpent", "1499", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 6, "Tiers", "Min total spent for Ultra Bubble tier", "TierUltraBubbleMinSpent", "1500", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 7, "Tiers", "Points multiplier for Bubble tier", "TierBubbleMultiplier", "1.0", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 8, "Tiers", "Points multiplier for Super Bubble tier", "TierSuperBubbleMultiplier", "1.5", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 9, "Tiers", "Points multiplier for Ultra Bubble tier", "TierUltraBubbleMultiplier", "2.0", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 10, "Tiers", "Permanent discount % for Ultra Bubble (0 = disabled)", "UltraBubblePermanentDiscountPercent", "0", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 11, "Redemption", "Dollar credit for 200 points", "Redemption200Points", "10", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 12, "Redemption", "Dollar credit for 500 points", "Redemption500Points", "30", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 13, "Redemption", "Dollar credit for 1000 points", "Redemption1000Points", "70", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 14, "Redemption", "Minimum order $ to use points", "RedemptionMinOrderAmount", "100", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 15, "Redemption", "Max % of order that can be paid with points", "RedemptionMaxOrderPercent", "30", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 16, "Bonuses", "Points given on first registration", "WelcomeBonusPoints", "50", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 17, "Bonuses", "Enable/disable welcome bonus", "WelcomeBonusEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 18, "Bonuses", "Extra % points for recurring customers", "RecurringBonusPercent", "25", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 19, "Bonuses", "Enable/disable recurring bonus", "RecurringBonusEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 20, "Bonuses", "Points for leaving a Google review", "ReviewBonusPoints", "40", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 21, "Bonuses", "Enable/disable review bonus", "ReviewBonusEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 22, "Bonuses", "Extra % points if booked within N days", "NextOrderBoosterPercent", "20", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 23, "Bonuses", "Days window for next order booster", "NextOrderBoosterDays", "7", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 24, "Bonuses", "Enable/disable next order booster", "NextOrderBoosterEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 25, "Streak", "Enable/disable streak system", "StreakEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 26, "Streak", "Bonus points for 3 consecutive bookings", "Streak3ConsecutiveBonus", "50", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 27, "Streak", "Bonus points for 6 consecutive bookings", "Streak6ConsecutiveBonus", "100", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 28, "Streak", "Max days between orders to keep streak", "StreakMaxDaysBetweenOrders", "45", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 29, "Referral", "Enable/disable referral system", "ReferralEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 30, "Referral", "Give points when referred user registers (DISABLED by default)", "ReferralRegistrationBonusEnabled", "false", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 31, "Referral", "Points for referrer when referred user registers", "ReferralRegistrationBonusPoints", "50", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 32, "Referral", "Dollar credit for referrer when referred user completes first order", "ReferralOrderCompletedCreditAmount", "20", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 33, "Referral", "Enable/disable order completion bonus", "ReferralOrderCompletedBonusEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 34, "Referral", "Extra bonus points for new user who used referral code", "ReferralNewUserBonusPoints", "0", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 35, "Referral", "Enable/disable extra bonus for referred new user", "ReferralNewUserBonusEnabled", "false", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5972));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5974));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5976));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5978));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5980));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5982));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5984));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5985));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5987));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1113));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1115));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5908));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5933));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5937));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5953));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5955));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1004));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1007));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1009));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1010));

            migrationBuilder.CreateIndex(
                name: "IX_Users_ReferralCode",
                table: "Users",
                column: "ReferralCode",
                unique: true,
                filter: "ReferralCode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ReferredByUserId",
                table: "Users",
                column: "ReferredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BubblePointsHistories_OrderId",
                table: "BubblePointsHistories",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_BubblePointsHistory_CreatedAt",
                table: "BubblePointsHistories",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BubblePointsHistory_UserId",
                table: "BubblePointsHistories",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BubbleRewardsSettings_Key",
                table: "BubbleRewardsSettings",
                column: "SettingKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredUserId",
                table: "Referrals",
                column: "ReferredUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerUserId",
                table: "Referrals",
                column: "ReferrerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ReferredByUserId",
                table: "Users",
                column: "ReferredByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ReferredByUserId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "BubblePointsHistories");

            migrationBuilder.DropTable(
                name: "BubbleRewardsSettings");

            migrationBuilder.DropTable(
                name: "Referrals");

            migrationBuilder.DropIndex(
                name: "IX_Users_ReferralCode",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_ReferredByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BubbleCredits",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BubblePoints",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ConsecutiveOrderCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastCompletedOrderDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferredByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReviewBonusGranted",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TotalSpentAmount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WelcomeBonusGranted",
                table: "Users");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3137));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3140));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3141));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3143));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3145));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3147));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3149));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3150));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3152));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 575, DateTimeKind.Utc).AddTicks(8446));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 575, DateTimeKind.Utc).AddTicks(8448));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3095));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3098));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3102));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3119));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 576, DateTimeKind.Utc).AddTicks(3121));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 575, DateTimeKind.Utc).AddTicks(8325));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 575, DateTimeKind.Utc).AddTicks(8329));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 575, DateTimeKind.Utc).AddTicks(8330));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 2, 8, 54, 43, 575, DateTimeKind.Utc).AddTicks(8331));
        }
    }
}
