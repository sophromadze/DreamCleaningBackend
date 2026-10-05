using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringServiceDaysCountAndContractLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContractId",
                table: "RecurringOrderSeries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceDaysOfMonth",
                table: "RecurringOrderSeries",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ServiceDaysOfWeek",
                table: "RecurringOrderSeries",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "UpcomingOccurrenceTarget",
                table: "RecurringOrderSeries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContractId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrderSeries_ContractId",
                table: "RecurringOrderSeries",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ContractId",
                table: "Orders",
                column: "ContractId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Contracts_ContractId",
                table: "Orders",
                column: "ContractId",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringOrderSeries_Contracts_ContractId",
                table: "RecurringOrderSeries",
                column: "ContractId",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Contracts_ContractId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringOrderSeries_Contracts_ContractId",
                table: "RecurringOrderSeries");

            migrationBuilder.DropIndex(
                name: "IX_RecurringOrderSeries_ContractId",
                table: "RecurringOrderSeries");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ContractId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "RecurringOrderSeries");

            migrationBuilder.DropColumn(
                name: "ServiceDaysOfMonth",
                table: "RecurringOrderSeries");

            migrationBuilder.DropColumn(
                name: "ServiceDaysOfWeek",
                table: "RecurringOrderSeries");

            migrationBuilder.DropColumn(
                name: "UpcomingOccurrenceTarget",
                table: "RecurringOrderSeries");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "Orders");
        }
    }
}
