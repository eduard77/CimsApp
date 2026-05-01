using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskQualitativeAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AssessedAt",
                table: "Risks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssessedById",
                table: "Risks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualitativeNotes",
                table: "Risks",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Risks_AssessedById",
                table: "Risks",
                column: "AssessedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Risks_Users_AssessedById",
                table: "Risks",
                column: "AssessedById",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Risks_Users_AssessedById",
                table: "Risks");

            migrationBuilder.DropIndex(
                name: "IX_Risks_AssessedById",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "AssessedAt",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "AssessedById",
                table: "Risks");

            migrationBuilder.DropColumn(
                name: "QualitativeNotes",
                table: "Risks");
        }
    }
}
