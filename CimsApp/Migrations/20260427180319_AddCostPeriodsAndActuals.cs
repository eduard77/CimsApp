using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCostPeriodsAndActuals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostPeriods_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ActualCosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostBreakdownItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActualCosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActualCosts_CostBreakdownItems_CostBreakdownItemId",
                        column: x => x.CostBreakdownItemId,
                        principalTable: "CostBreakdownItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ActualCosts_CostPeriods_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "CostPeriods",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ActualCosts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActualCosts_CostBreakdownItemId",
                table: "ActualCosts",
                column: "CostBreakdownItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ActualCosts_PeriodId",
                table: "ActualCosts",
                column: "PeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_ActualCosts_ProjectId_CostBreakdownItemId",
                table: "ActualCosts",
                columns: new[] { "ProjectId", "CostBreakdownItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActualCosts_ProjectId_PeriodId",
                table: "ActualCosts",
                columns: new[] { "ProjectId", "PeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_CostPeriods_ProjectId_StartDate",
                table: "CostPeriods",
                columns: new[] { "ProjectId", "StartDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActualCosts");

            migrationBuilder.DropTable(
                name: "CostPeriods");
        }
    }
}
