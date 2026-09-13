using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    /// <summary>Adds CanReceiveEmails and CanReceiveMessages so users can opt in/out of emails and messages separately. Backfills from CanReceiveCommunications.</summary>
    public partial class AddCanReceiveEmailsAndMessagesToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanReceiveEmails",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanReceiveMessages",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            // Backfill from existing CanReceiveCommunications
            migrationBuilder.Sql("UPDATE Users SET CanReceiveEmails = CanReceiveCommunications, CanReceiveMessages = CanReceiveCommunications;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanReceiveEmails",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanReceiveMessages",
                table: "Users");
        }
    }
}
