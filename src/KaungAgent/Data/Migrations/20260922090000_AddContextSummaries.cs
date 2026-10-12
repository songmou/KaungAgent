using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaungAgent.Data.Migrations;

[DbContext(typeof(KaungAgentDbContext))]
[Migration("20260922090000_AddContextSummaries")]
public sealed class AddContextSummaries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "SummarizesThroughMessageId",
            table: "ChatMessages",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ChatMessages_ConversationId_Status_Id",
            table: "ChatMessages",
            columns: new[] { "ConversationId", "Status", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ChatMessages_ConversationId_Status_Id",
            table: "ChatMessages");

        migrationBuilder.DropColumn(
            name: "SummarizesThroughMessageId",
            table: "ChatMessages");
    }
}
