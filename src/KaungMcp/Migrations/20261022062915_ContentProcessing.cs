using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaungMcp.Migrations
{
    /// <inheritdoc />
    public partial class ContentProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "TaskId",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "InputFileId",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmitterId",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmitterKind",
                table: "TaskRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContentFiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: true),
                    OwnerKind = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_InputFileId",
                table: "TaskRuns",
                column: "InputFileId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_SubmitterKind_SubmitterId_CreatedTicks",
                table: "TaskRuns",
                columns: new[] { "SubmitterKind", "SubmitterId", "CreatedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentFiles_ExpiresTicks",
                table: "ContentFiles",
                column: "ExpiresTicks");

            migrationBuilder.CreateIndex(
                name: "IX_ContentFiles_OwnerKind_OwnerId_ExpiresTicks",
                table: "ContentFiles",
                columns: new[] { "OwnerKind", "OwnerId", "ExpiresTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Old schemas cannot represent one-off runs without a scheduled plan.
            migrationBuilder.Sql("DELETE FROM TaskRuns WHERE TaskId IS NULL");
            migrationBuilder.DropTable(
                name: "ContentFiles");

            migrationBuilder.DropIndex(
                name: "IX_TaskRuns_InputFileId",
                table: "TaskRuns");

            migrationBuilder.DropIndex(
                name: "IX_TaskRuns_SubmitterKind_SubmitterId_CreatedTicks",
                table: "TaskRuns");

            migrationBuilder.DropColumn(
                name: "InputFileId",
                table: "TaskRuns");

            migrationBuilder.DropColumn(
                name: "SubmitterId",
                table: "TaskRuns");

            migrationBuilder.DropColumn(
                name: "SubmitterKind",
                table: "TaskRuns");

            migrationBuilder.AlterColumn<Guid>(
                name: "TaskId",
                table: "TaskRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
