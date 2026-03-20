using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class ddasonditionoxtraervice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasCondition",
                table: "ExtraServices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8547), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8551), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8554), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8557), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8559), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8564), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8566), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8568), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8570), false });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 456, DateTimeKind.Local).AddTicks(6336));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 456, DateTimeKind.Local).AddTicks(6339));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8457));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8466));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8470));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8508));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 457, DateTimeKind.Local).AddTicks(8512));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 456, DateTimeKind.Local).AddTicks(5928));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 456, DateTimeKind.Local).AddTicks(5944));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 456, DateTimeKind.Local).AddTicks(5946));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 12, 32, 456, DateTimeKind.Local).AddTicks(5948));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasCondition",
                table: "ExtraServices");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3674));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3678));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3680));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3683));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3685));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3688));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3691));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3693));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3695));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 632, DateTimeKind.Local).AddTicks(3290));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 632, DateTimeKind.Local).AddTicks(3294));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3591));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3599));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3605));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3639));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 633, DateTimeKind.Local).AddTicks(3642));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 632, DateTimeKind.Local).AddTicks(3050));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 632, DateTimeKind.Local).AddTicks(3068));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 632, DateTimeKind.Local).AddTicks(3069));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 2, 0, 40, 19, 632, DateTimeKind.Local).AddTicks(3071));
        }
    }
}
