using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketDataSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MarketDataJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RequestedFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RequestedTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedSteps = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSteps = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordsAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordsUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    ResumeFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    LeaseExpiresUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    NotBeforeUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketDataSyncStates",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EarliestCachedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LatestCachedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EarliestRequestedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LatestRequestedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastSuccessfulSyncUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAttemptUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataSyncStates", x => new { x.Symbol, x.Source });
                });

            migrationBuilder.CreateTable(
                name: "UnavailableSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnavailableSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataJobs_Status_CreatedAtUtc",
                table: "MarketDataJobs",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataJobs_Symbol_Source_Status",
                table: "MarketDataJobs",
                columns: new[] { "Symbol", "Source", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UnavailableSessions_Symbol_Source_SessionDate",
                table: "UnavailableSessions",
                columns: new[] { "Symbol", "Source", "SessionDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationSettings");

            migrationBuilder.DropTable(
                name: "MarketDataJobs");

            migrationBuilder.DropTable(
                name: "MarketDataSyncStates");

            migrationBuilder.DropTable(
                name: "UnavailableSessions");
        }
    }
}
