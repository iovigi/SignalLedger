using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBacktestDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EquityPoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BacktestRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Series = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Equity = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Cash = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Units = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquityPoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BacktestRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SignalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExecutionDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    EffectivePrice = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Units = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Notional = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Commission = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    CashAfter = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    EquityAfter = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EquityPoints_BacktestRunId_Series_SessionDate",
                table: "EquityPoints",
                columns: new[] { "BacktestRunId", "Series", "SessionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_BacktestRunId_Sequence",
                table: "Trades",
                columns: new[] { "BacktestRunId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EquityPoints");

            migrationBuilder.DropTable(
                name: "Trades");
        }
    }
}
