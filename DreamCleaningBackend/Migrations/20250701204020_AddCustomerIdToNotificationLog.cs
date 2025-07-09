using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerIdToNotificationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "CleanerId",
                table: "NotificationLogs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "NotificationLogs",
                type: "int",
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_CleanerId",
                table: "NotificationLogs",
                column: "CleanerId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_CustomerId",
                table: "NotificationLogs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_OrderId",
                table: "NotificationLogs",
                column: "OrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_Orders_OrderId",
                table: "NotificationLogs",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_Users_CleanerId",
                table: "NotificationLogs",
                column: "CleanerId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_Users_CustomerId",
                table: "NotificationLogs",
                column: "CustomerId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLogs_Orders_OrderId",
                table: "NotificationLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLogs_Users_CleanerId",
                table: "NotificationLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLogs_Users_CustomerId",
                table: "NotificationLogs");

            migrationBuilder.DropIndex(
                name: "IX_NotificationLogs_CleanerId",
                table: "NotificationLogs");

            migrationBuilder.DropIndex(
                name: "IX_NotificationLogs_CustomerId",
                table: "NotificationLogs");

            migrationBuilder.DropIndex(
                name: "IX_NotificationLogs_OrderId",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "NotificationLogs");

            migrationBuilder.AlterColumn<int>(
                name: "CleanerId",
                table: "NotificationLogs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6621));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6624));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6627));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6631));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6633));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6635));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6638));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6639));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6641));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 787, DateTimeKind.Local).AddTicks(6153));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 787, DateTimeKind.Local).AddTicks(6156));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6537));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6544));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6549));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6585));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 788, DateTimeKind.Local).AddTicks(6587));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 787, DateTimeKind.Local).AddTicks(5846));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 787, DateTimeKind.Local).AddTicks(5861));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 787, DateTimeKind.Local).AddTicks(5863));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 1, 22, 59, 8, 787, DateTimeKind.Local).AddTicks(5864));
        }
    }
}
