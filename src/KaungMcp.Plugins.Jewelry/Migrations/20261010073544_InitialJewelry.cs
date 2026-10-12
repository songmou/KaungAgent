using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaungMcp.Plugins.Jewelry.Migrations
{
    /// <inheritdoc />
    public partial class InitialJewelry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JewelrySources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JewelrySources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JewelryCollectionAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    QuoteCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JewelryCollectionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JewelryCollectionAttempts_JewelrySources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "JewelrySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JewelryCollectionStates",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    LastAttemptTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    NextAttemptTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JewelryCollectionStates", x => x.SourceId);
                    table.ForeignKey(
                        name: "FK_JewelryCollectionStates_JewelrySources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "JewelrySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JewelryQuotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesKey = table.Column<string>(type: "TEXT", nullable: false),
                    SourceDate = table.Column<string>(type: "TEXT", nullable: true),
                    CollectedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JewelryQuotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JewelryQuotes_JewelrySources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "JewelrySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JewelryCollectionAttempts_SourceId_StartedTicks",
                table: "JewelryCollectionAttempts",
                columns: new[] { "SourceId", "StartedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_JewelryQuotes_InstrumentId_CollectedTicks",
                table: "JewelryQuotes",
                columns: new[] { "InstrumentId", "CollectedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_JewelryQuotes_InstrumentId_SourceDate_CollectedTicks",
                table: "JewelryQuotes",
                columns: new[] { "InstrumentId", "SourceDate", "CollectedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_JewelryQuotes_SeriesKey_SourceDate_CollectedTicks",
                table: "JewelryQuotes",
                columns: new[] { "SeriesKey", "SourceDate", "CollectedTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_JewelryQuotes_SourceId",
                table: "JewelryQuotes",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JewelryCollectionAttempts");

            migrationBuilder.DropTable(
                name: "JewelryCollectionStates");

            migrationBuilder.DropTable(
                name: "JewelryQuotes");

            migrationBuilder.DropTable(
                name: "JewelrySources");
        }
    }
}
