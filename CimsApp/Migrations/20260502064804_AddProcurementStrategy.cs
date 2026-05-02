using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddProcurementStrategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcurementStrategies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Approach = table.Column<int>(type: "int", nullable: false),
                    ContractForm = table.Column<int>(type: "int", nullable: false),
                    EstimatedTotalValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    KeyDates = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PackageBreakdownNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementStrategies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcurementStrategies_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProcurementStrategies_Users_ApprovedById",
                        column: x => x.ApprovedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementStrategies_ApprovedById",
                table: "ProcurementStrategies",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementStrategies_ProjectId",
                table: "ProcurementStrategies",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcurementStrategies");
        }
    }
}
