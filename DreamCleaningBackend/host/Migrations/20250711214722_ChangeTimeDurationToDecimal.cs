using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class ChangeTimeDurationToDecimal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "TimeDuration",
                table: "ServiceTypes",
                type: "decimal(65,30)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "TimeDuration",
                table: "Services",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "Duration",
                table: "OrderServices",
                type: "decimal(65,30)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "TotalDuration",
                table: "Orders",
                type: "decimal(65,30)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "Duration",
                table: "OrderExtraServices",
                type: "decimal(65,30)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "Duration",
                table: "ExtraServices",
                type: "decimal(65,30)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 410, DateTimeKind.Local).AddTicks(9999), 60m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(2), 120m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(5), 0m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(7), 20m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(10), 30m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(14), 30m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(17), 45m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(19), 30m });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 411, DateTimeKind.Local).AddTicks(21), 45m });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 409, DateTimeKind.Local).AddTicks(9816), 90m });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 409, DateTimeKind.Local).AddTicks(9820), 120m });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 410, DateTimeKind.Local).AddTicks(9871), 30m });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 410, DateTimeKind.Local).AddTicks(9881), 45m });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 410, DateTimeKind.Local).AddTicks(9886), 1m });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 410, DateTimeKind.Local).AddTicks(9925), 0m });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 12, 1, 47, 21, 410, DateTimeKind.Local).AddTicks(9928), 60m });

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 12, 1, 47, 21, 409, DateTimeKind.Local).AddTicks(9521));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 12, 1, 47, 21, 409, DateTimeKind.Local).AddTicks(9540));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 12, 1, 47, 21, 409, DateTimeKind.Local).AddTicks(9590));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 12, 1, 47, 21, 409, DateTimeKind.Local).AddTicks(9592));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "TimeDuration",
                table: "ServiceTypes",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(65,30)");

            migrationBuilder.AlterColumn<int>(
                name: "TimeDuration",
                table: "Services",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<int>(
                name: "Duration",
                table: "OrderServices",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(65,30)");

            migrationBuilder.AlterColumn<int>(
                name: "TotalDuration",
                table: "Orders",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(65,30)");

            migrationBuilder.AlterColumn<int>(
                name: "Duration",
                table: "OrderExtraServices",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(65,30)");

            migrationBuilder.AlterColumn<int>(
                name: "Duration",
                table: "ExtraServices",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(65,30)");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3974), 60 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3977), 120 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3980), 0 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3983), 20 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3985), 30 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3988), 30 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3991), 45 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3993), 30 });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "CreatedAt", "Duration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3995), 45 });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 147, DateTimeKind.Local).AddTicks(9017), 90 });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 147, DateTimeKind.Local).AddTicks(9021), 120 });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3859), 30 });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3868), 45 });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3875), 1 });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3930), 0 });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "CreatedAt", "TimeDuration" },
                values: new object[] { new DateTime(2025, 7, 6, 0, 44, 46, 149, DateTimeKind.Local).AddTicks(3934), 60 });

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 6, 0, 44, 46, 147, DateTimeKind.Local).AddTicks(8591));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 6, 0, 44, 46, 147, DateTimeKind.Local).AddTicks(8609));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 6, 0, 44, 46, 147, DateTimeKind.Local).AddTicks(8612));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 6, 0, 44, 46, 147, DateTimeKind.Local).AddTicks(8614));
        }
    }
}
