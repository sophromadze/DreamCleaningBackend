using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCallClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallCategory",
                table: "CallRecords",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "MatchedCleanerId",
                table: "CallRecords",
                type: "int",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6334));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6337));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6338));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6340));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6341));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6343));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6345));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6349));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6350));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 665, DateTimeKind.Utc).AddTicks(9123));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 665, DateTimeKind.Utc).AddTicks(9125));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6273));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6276));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6279));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6305));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 666, DateTimeKind.Utc).AddTicks(6307));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 665, DateTimeKind.Utc).AddTicks(8936));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 665, DateTimeKind.Utc).AddTicks(8940));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 665, DateTimeKind.Utc).AddTicks(8941));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 8, 36, 33, 665, DateTimeKind.Utc).AddTicks(8943));

            migrationBuilder.CreateIndex(
                name: "IX_CallRecords_Category",
                table: "CallRecords",
                column: "CallCategory");

            migrationBuilder.CreateIndex(
                name: "IX_CallRecords_MatchedCleanerId",
                table: "CallRecords",
                column: "MatchedCleanerId");

            migrationBuilder.AddForeignKey(
                name: "FK_CallRecords_Cleaners_MatchedCleanerId",
                table: "CallRecords",
                column: "MatchedCleanerId",
                principalTable: "Cleaners",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CallRecords_Cleaners_MatchedCleanerId",
                table: "CallRecords");

            migrationBuilder.DropIndex(
                name: "IX_CallRecords_Category",
                table: "CallRecords");

            migrationBuilder.DropIndex(
                name: "IX_CallRecords_MatchedCleanerId",
                table: "CallRecords");

            migrationBuilder.DropColumn(
                name: "CallCategory",
                table: "CallRecords");

            migrationBuilder.DropColumn(
                name: "MatchedCleanerId",
                table: "CallRecords");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8830));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8832));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8835));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8836));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8838));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8840));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8841));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8846));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8848));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(2365));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(2367));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8775));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8778));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8781));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8803));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(8805));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(2214));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(2219));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(2220));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 0, 37, 11, 515, DateTimeKind.Utc).AddTicks(2221));
        }
    }
}
