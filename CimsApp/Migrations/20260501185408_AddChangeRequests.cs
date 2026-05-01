using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Category = table.Column<int>(type: "int", nullable: false),
                    BsaCategory = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    RaisedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssessedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AssessmentNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecisionAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImplementedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EstimatedCostImpact = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    EstimatedTimeImpactDays = table.Column<int>(type: "int", nullable: true),
                    ProgrammeImpactSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CostImpactSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GeneratedVariationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Users_AssessedById",
                        column: x => x.AssessedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Users_DecisionById",
                        column: x => x.DecisionById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Users_RaisedById",
                        column: x => x.RaisedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Variations_GeneratedVariationId",
                        column: x => x.GeneratedVariationId,
                        principalTable: "Variations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_AssessedById",
                table: "ChangeRequests",
                column: "AssessedById");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_DecisionById",
                table: "ChangeRequests",
                column: "DecisionById");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_GeneratedVariationId",
                table: "ChangeRequests",
                column: "GeneratedVariationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_ProjectId_Number",
                table: "ChangeRequests",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_ProjectId_RaisedAt",
                table: "ChangeRequests",
                columns: new[] { "ProjectId", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_ProjectId_State",
                table: "ChangeRequests",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_RaisedById",
                table: "ChangeRequests",
                column: "RaisedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeRequests");
        }
    }
}
