using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExperimentCells",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Period = table.Column<int>(type: "INTEGER", nullable: false),
                    CommissionPercent = table.Column<decimal>(type: "TEXT", precision: 8, scale: 4, nullable: false),
                    StrategyReturn = table.Column<double>(type: "REAL", nullable: false),
                    BenchmarkReturn = table.Column<double>(type: "REAL", nullable: false),
                    NetOutperformance = table.Column<double>(type: "REAL", nullable: false),
                    WindowLabel = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    IsHoldout = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentCells", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedCells = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCells = table.Column<int>(type: "INTEGER", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentCells_ExperimentJobId_Period_CommissionPercent_WindowLabel",
                table: "ExperimentCells",
                columns: new[] { "ExperimentJobId", "Period", "CommissionPercent", "WindowLabel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExperimentCells");

            migrationBuilder.DropTable(
                name: "ExperimentJobs");
        }
    }
}
