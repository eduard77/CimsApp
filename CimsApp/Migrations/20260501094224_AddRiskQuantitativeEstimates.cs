using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskQuantitativeEstimates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BestCase",
                table: "Risks",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Distribution",
                table: "Risks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MostLikely",
                table: "Risks",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WorstCase",
                table: "Risks",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BestCase",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "Distribution",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "MostLikely",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "WorstCase",
                table: "Risks");
        }
    }
}
