using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCompensationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompensationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    NotifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EstimatedCostImpact = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    EstimatedTimeImpactDays = table.Column<int>(type: "int", nullable: true),
                    QuotedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuotedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    QuotationNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecisionAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImplementedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImplementedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompensationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompensationEvents_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompensationEvents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompensationEvents_Users_DecisionById",
                        column: x => x.DecisionById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompensationEvents_Users_ImplementedById",
                        column: x => x.ImplementedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompensationEvents_Users_NotifiedById",
                        column: x => x.NotifiedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompensationEvents_Users_QuotedById",
                        column: x => x.QuotedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_ContractId_State",
                table: "CompensationEvents",
                columns: new[] { "ContractId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_DecisionById",
                table: "CompensationEvents",
                column: "DecisionById");

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_ImplementedById",
                table: "CompensationEvents",
                column: "ImplementedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_NotifiedById",
                table: "CompensationEvents",
                column: "NotifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_ProjectId_NotifiedAt",
                table: "CompensationEvents",
                columns: new[] { "ProjectId", "NotifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_ProjectId_Number",
                table: "CompensationEvents",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompensationEvents_QuotedById",
                table: "CompensationEvents",
                column: "QuotedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompensationEvents");
        }
    }
}
