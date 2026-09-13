using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundSourceAndNullableRefundAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RefundedByUserId",
                table: "OrderRefunds",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "OrderRefunds",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stripe-imported refunds have no admin behind them and cannot survive
            // the column becoming non-nullable again.
            migrationBuilder.Sql("DELETE FROM `OrderRefunds` WHERE `RefundedByUserId` IS NULL;");
    
            migrationBuilder.DropColumn(
                name: "Source",
                table: "OrderRefunds");

            migrationBuilder.AlterColumn<int>(
                name: "RefundedByUserId",
                table: "OrderRefunds",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
