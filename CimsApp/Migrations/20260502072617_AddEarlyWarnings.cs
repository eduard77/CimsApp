using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEarlyWarnings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EarlyWarnings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    RaisedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResponseNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClosedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosureNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EarlyWarnings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EarlyWarnings_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EarlyWarnings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EarlyWarnings_Users_ClosedById",
                        column: x => x.ClosedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EarlyWarnings_Users_RaisedById",
                        column: x => x.RaisedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EarlyWarnings_Users_ReviewedById",
                        column: x => x.ReviewedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EarlyWarnings_ClosedById",
                table: "EarlyWarnings",
                column: "ClosedById");

            migrationBuilder.CreateIndex(
                name: "IX_EarlyWarnings_ContractId_State",
                table: "EarlyWarnings",
                columns: new[] { "ContractId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_EarlyWarnings_ProjectId_RaisedAt",
                table: "EarlyWarnings",
                columns: new[] { "ProjectId", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EarlyWarnings_RaisedById",
                table: "EarlyWarnings",
                column: "RaisedById");

            migrationBuilder.CreateIndex(
                name: "IX_EarlyWarnings_ReviewedById",
                table: "EarlyWarnings",
                column: "ReviewedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EarlyWarnings");
        }
    }
}
