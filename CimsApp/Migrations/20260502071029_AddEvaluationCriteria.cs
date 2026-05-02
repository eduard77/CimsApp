using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluationCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvaluationCriteria",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenderPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Weight = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationCriteria_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationCriteria_TenderPackages_TenderPackageId",
                        column: x => x.TenderPackageId,
                        principalTable: "TenderPackages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EvaluationScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriterionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScoredById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScoredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationScores_EvaluationCriteria_CriterionId",
                        column: x => x.CriterionId,
                        principalTable: "EvaluationCriteria",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationScores_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationScores_Tenders_TenderId",
                        column: x => x.TenderId,
                        principalTable: "Tenders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationScores_Users_ScoredById",
                        column: x => x.ScoredById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationCriteria_ProjectId",
                table: "EvaluationCriteria",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationCriteria_TenderPackageId",
                table: "EvaluationCriteria",
                column: "TenderPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationScores_CriterionId",
                table: "EvaluationScores",
                column: "CriterionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationScores_ProjectId",
                table: "EvaluationScores",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationScores_ScoredById",
                table: "EvaluationScores",
                column: "ScoredById");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationScores_TenderId_CriterionId",
                table: "EvaluationScores",
                columns: new[] { "TenderId", "CriterionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvaluationScores");

            migrationBuilder.DropTable(
                name: "EvaluationCriteria");
        }
    }
}
