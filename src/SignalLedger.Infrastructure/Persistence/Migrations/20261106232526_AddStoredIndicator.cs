using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredIndicator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Indicator",
                table: "EquityPoints",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Indicator",
                table: "EquityPoints");
        }
    }
}
