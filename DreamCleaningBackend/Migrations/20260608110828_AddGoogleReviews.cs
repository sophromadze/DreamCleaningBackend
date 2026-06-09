using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleReviews",
                columns: table => new
                {
                    ReviewId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AuthorName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProfilePhotoUrl = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReplyText = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdateTime = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IsHidden = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleReviews", x => x.ReviewId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6941));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6944));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6945));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6947));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6949));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6950));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6952));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6956));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6958));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(683));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(686));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6893));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6896));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6899));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6923));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(6925));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(572));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(576));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(577));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 8, 11, 8, 27, 943, DateTimeKind.Utc).AddTicks(578));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleReviews");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8818));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8820));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8822));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8824));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8826));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8828));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8829));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8835));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8837));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(2495));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(2497));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8767));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8770));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8775));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8798));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(8800));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(2367));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(2372));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(2374));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 3, 10, 18, 17, 343, DateTimeKind.Utc).AddTicks(2375));
        }
    }
}
