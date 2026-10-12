using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaungAgent.Data.Migrations;

[DbContext(typeof(KaungAgentDbContext))]
[Migration("20260920050000_InitialAgentWorkspace")]
public sealed class InitialAgentWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpServers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                ProtectedHeadersJson = table.Column<string>(type: "TEXT", nullable: true),
                TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                IsManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                ManagedKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                CapabilitySnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                LastCheckedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastSyncedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            }, constraints: table => table.PrimaryKey("PK_McpServers", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ModelProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                BaseUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ProtectedApiKey = table.Column<string>(type: "TEXT", nullable: true),
                ProtectedHeadersJson = table.Column<string>(type: "TEXT", nullable: true),
                Temperature = table.Column<double>(type: "REAL", nullable: false),
                MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            }, constraints: table => table.PrimaryKey("PK_ModelProfiles", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Conversations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ModelProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolSelectionJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("PK_Conversations", x => x.Id);
                table.ForeignKey("FK_Conversations_ModelProfiles_ModelProfileId", x => x.ModelProfileId, "ModelProfiles", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ChatMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Content = table.Column<string>(type: "TEXT", nullable: true),
                ToolCallId = table.Column<string>(type: "TEXT", nullable: true),
                ToolName = table.Column<string>(type: "TEXT", nullable: true),
                ToolCallsJson = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                Error = table.Column<string>(type: "TEXT", nullable: true),
                InputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                OutputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                TotalTokens = table.Column<long>(type: "INTEGER", nullable: true),
                DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("PK_ChatMessages", x => x.Id);
                table.ForeignKey("FK_ChatMessages_Conversations_ConversationId", x => x.ConversationId, "Conversations", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ToolExecutions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                ChatMessageId = table.Column<long>(type: "INTEGER", nullable: false),
                McpServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolCallId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ToolName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                ArgumentsJson = table.Column<string>(type: "TEXT", nullable: false),
                ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                Error = table.Column<string>(type: "TEXT", nullable: true),
                DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("PK_ToolExecutions", x => x.Id);
                table.ForeignKey("FK_ToolExecutions_ChatMessages_ChatMessageId", x => x.ChatMessageId, "ChatMessages", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_McpServers_ManagedKey", "McpServers", "ManagedKey", unique: true);
        migrationBuilder.CreateIndex("IX_McpServers_Name", "McpServers", "Name", unique: true);
        migrationBuilder.CreateIndex("IX_ModelProfiles_IsDefault", "ModelProfiles", "IsDefault");
        migrationBuilder.CreateIndex("IX_ModelProfiles_Name", "ModelProfiles", "Name", unique: true);
        migrationBuilder.CreateIndex("IX_Conversations_ModelProfileId", "Conversations", "ModelProfileId");
        migrationBuilder.CreateIndex("IX_Conversations_UpdatedAtUtc", "Conversations", "UpdatedAtUtc");
        migrationBuilder.CreateIndex("IX_ChatMessages_ConversationId_Id", "ChatMessages", new[] { "ConversationId", "Id" });
        migrationBuilder.CreateIndex("IX_ToolExecutions_ChatMessageId", "ToolExecutions", "ChatMessageId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("McpServers");
        migrationBuilder.DropTable("ToolExecutions");
        migrationBuilder.DropTable("ChatMessages");
        migrationBuilder.DropTable("Conversations");
        migrationBuilder.DropTable("ModelProfiles");
    }
}
