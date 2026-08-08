using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturerExtraction.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStoredMonthlyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthlyCommission",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "MonthlyLineCount",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "MonthlyNetSales",
                table: "Documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyCommission",
                table: "Documents",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyLineCount",
                table: "Documents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyNetSales",
                table: "Documents",
                type: "decimal(18,2)",
                nullable: true);
        }
    }
}
