using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCostBreakdownItemSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PercentComplete",
                table: "CostBreakdownItems",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledEnd",
                table: "CostBreakdownItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledStart",
                table: "CostBreakdownItems",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PercentComplete",
                table: "CostBreakdownItems");

            migrationBuilder.DropColumn(
                name: "ScheduledEnd",
                table: "CostBreakdownItems");

            migrationBuilder.DropColumn(
                name: "ScheduledStart",
                table: "CostBreakdownItems");
        }
    }
}
