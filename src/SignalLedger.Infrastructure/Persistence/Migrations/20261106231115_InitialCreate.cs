using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EngineVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DataFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    To = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    InitialCapital = table.Column<decimal>(type: "TEXT", precision: 20, scale: 4, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Open = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: false),
                    High = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: false),
                    Low = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: false),
                    AdjustedClose = table.Column<decimal>(type: "TEXT", precision: 20, scale: 8, nullable: true),
                    Volume = table.Column<long>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsFinal = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyBars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RequestedTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ActualFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ActualTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RecordsAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordsUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordsRejected = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Warning = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderMetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    AssetClass = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ExchangeTimeZone = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PriceSeriesMeaning = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastDate = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.Symbol);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_DataFingerprint_ParametersJson",
                table: "BacktestRuns",
                columns: new[] { "DataFingerprint", "ParametersJson" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyBars_Symbol_SessionDate_Source",
                table: "DailyBars",
                columns: new[] { "Symbol", "SessionDate", "Source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestRuns");

            migrationBuilder.DropTable(
                name: "DailyBars");

            migrationBuilder.DropTable(
                name: "ImportRuns");

            migrationBuilder.DropTable(
                name: "Instruments");
        }
    }
}
