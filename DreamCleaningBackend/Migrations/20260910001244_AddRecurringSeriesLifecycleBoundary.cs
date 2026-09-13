using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringSeriesLifecycleBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GenerateAfterDate",
                table: "RecurringOrderSeries",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StoppedAt",
                table: "RecurringOrderSeries",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenerateAfterDate",
                table: "RecurringOrderSeries");

            migrationBuilder.DropColumn(
                name: "StoppedAt",
                table: "RecurringOrderSeries");
        }
    }
}
