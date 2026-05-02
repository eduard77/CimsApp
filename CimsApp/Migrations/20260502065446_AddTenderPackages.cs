using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenderPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenderPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EstimatedValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    IssueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReturnDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IssuedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AwardedTenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenderPackages_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenderPackages_Users_ClosedById",
                        column: x => x.ClosedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenderPackages_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenderPackages_Users_IssuedById",
                        column: x => x.IssuedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenderPackages_ClosedById",
                table: "TenderPackages",
                column: "ClosedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenderPackages_CreatedById",
                table: "TenderPackages",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenderPackages_IssuedById",
                table: "TenderPackages",
                column: "IssuedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenderPackages_ProjectId_IsActive",
                table: "TenderPackages",
                columns: new[] { "ProjectId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TenderPackages_ProjectId_Number",
                table: "TenderPackages",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenderPackages_ProjectId_State",
                table: "TenderPackages",
                columns: new[] { "ProjectId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenderPackages");
        }
    }
}
