using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaungMcp.Migrations
{
    /// <inheritdoc />
    public partial class InitialPluginHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpClientApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpClientApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpToolSettings",
                columns: table => new
                {
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpToolSettings", x => x.ToolName);
                });

            migrationBuilder.CreateTable(
                name: "PluginStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<string>(type: "TEXT", nullable: true),
                    ArgumentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    RiskSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    RiskConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    BlockedReason = table.Column<string>(type: "TEXT", nullable: true),
                    LastRunStatus = table.Column<string>(type: "TEXT", nullable: true),
                    MisfireSummary = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpInvocationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientApplicationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RequestMethod = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "tools/call"),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "mcp_client"),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequestParametersJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    BillableUnits = table.Column<long>(type: "INTEGER", nullable: true),
                    CostMicros = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpInvocationLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpInvocationLogs_McpClientApplications_ClientApplicationId",
                        column: x => x.ClientApplicationId,
                        principalTable: "McpClientApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: false),
                    PluginVersion = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ArgumentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PlannedTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryAtTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    RiskSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    RiskConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ArtifactJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    InvocationLogId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRuns_ScheduledTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ScheduledTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpClientApplications_KeyId",
                table: "McpClientApplications",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpClientApplications_Name",
                table: "McpClientApplications",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpInvocationLogs_ClientApplicationId_StartedAtUtc",
                table: "McpInvocationLogs",
                columns: new[] { "ClientApplicationId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpInvocationLogs_RequestMethod_StartedAtUtc",
                table: "McpInvocationLogs",
                columns: new[] { "RequestMethod", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpInvocationLogs_Source_StartedAtUtc",
                table: "McpInvocationLogs",
                columns: new[] { "Source", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpInvocationLogs_StartedAtUtc",
                table: "McpInvocationLogs",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_McpInvocationLogs_ToolName_StartedAtUtc",
                table: "McpInvocationLogs",
                columns: new[] { "ToolName", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_IsEnabled_IsDeleted_NextRunTicks",
                table: "ScheduledTasks",
                columns: new[] { "IsEnabled", "IsDeleted", "NextRunTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_PluginId_TemplateId",
                table: "ScheduledTasks",
                columns: new[] { "PluginId", "TemplateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_Status_RetryAtTicks",
                table: "TaskRuns",
                columns: new[] { "Status", "RetryAtTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_TaskId_PlannedTicks",
                table: "TaskRuns",
                columns: new[] { "TaskId", "PlannedTicks" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_TaskId_StartedTicks",
                table: "TaskRuns",
                columns: new[] { "TaskId", "StartedTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpInvocationLogs");

            migrationBuilder.DropTable(
                name: "McpToolSettings");

            migrationBuilder.DropTable(
                name: "PluginStates");

            migrationBuilder.DropTable(
                name: "TaskRuns");

            migrationBuilder.DropTable(
                name: "McpClientApplications");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");
        }
    }
}
