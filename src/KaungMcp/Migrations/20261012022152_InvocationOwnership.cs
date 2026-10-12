using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaungMcp.Migrations
{
    /// <inheritdoc />
    public partial class InvocationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "McpInvocationLogs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerKind",
                table: "McpInvocationLogs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpInvocationLogs_OwnerKind_OwnerId_StartedAtUtc",
                table: "McpInvocationLogs",
                columns: new[] { "OwnerKind", "OwnerId", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McpInvocationLogs_OwnerKind_OwnerId_StartedAtUtc",
                table: "McpInvocationLogs");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "McpInvocationLogs");

            migrationBuilder.DropColumn(
                name: "OwnerKind",
                table: "McpInvocationLogs");
        }
    }
}
