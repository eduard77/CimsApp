using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TenderPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AwardedTenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContractorOrganisation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContractValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ContractForm = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    AwardNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AwardedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AwardedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contracts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Contracts_TenderPackages_TenderPackageId",
                        column: x => x.TenderPackageId,
                        principalTable: "TenderPackages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Contracts_Tenders_AwardedTenderId",
                        column: x => x.AwardedTenderId,
                        principalTable: "Tenders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Contracts_Users_AwardedById",
                        column: x => x.AwardedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Contracts_Users_ClosedById",
                        column: x => x.ClosedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_AwardedById",
                table: "Contracts",
                column: "AwardedById");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_AwardedTenderId",
                table: "Contracts",
                column: "AwardedTenderId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_ClosedById",
                table: "Contracts",
                column: "ClosedById");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_ProjectId_Number",
                table: "Contracts",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_ProjectId_State",
                table: "Contracts",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenderPackageId",
                table: "Contracts",
                column: "TenderPackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Contracts");
        }
    }
}
