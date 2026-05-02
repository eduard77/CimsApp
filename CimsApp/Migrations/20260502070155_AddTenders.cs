using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tenders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenderPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BidderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BidderOrganisation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    StateNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GeneratedContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenders_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tenders_TenderPackages_TenderPackageId",
                        column: x => x.TenderPackageId,
                        principalTable: "TenderPackages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tenders_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenders_CreatedById",
                table: "Tenders",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Tenders_ProjectId_SubmittedAt",
                table: "Tenders",
                columns: new[] { "ProjectId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenders_TenderPackageId_State",
                table: "Tenders",
                columns: new[] { "TenderPackageId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tenders");
        }
    }
}
