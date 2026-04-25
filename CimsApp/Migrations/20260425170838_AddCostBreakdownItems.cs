using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCostBreakdownItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostBreakdownItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostBreakdownItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostBreakdownItems_CostBreakdownItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CostBreakdownItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CostBreakdownItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostBreakdownItems_ParentId",
                table: "CostBreakdownItems",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_CostBreakdownItems_ProjectId_Code",
                table: "CostBreakdownItems",
                columns: new[] { "ProjectId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostBreakdownItems_ProjectId_ParentId",
                table: "CostBreakdownItems",
                columns: new[] { "ProjectId", "ParentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostBreakdownItems");
        }
    }
}
