using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMidpTidp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MidpEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DocTypeFilter = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsCompleted = table.Column<bool>(type: "bit", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MidpEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MidpEntries_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MidpEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MidpEntries_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TidpEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MidpEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsSignedOff = table.Column<bool>(type: "bit", nullable: false),
                    SignedOffById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SignedOffAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SignOffNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TidpEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TidpEntries_MidpEntries_MidpEntryId",
                        column: x => x.MidpEntryId,
                        principalTable: "MidpEntries",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TidpEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TidpEntries_Users_SignedOffById",
                        column: x => x.SignedOffById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MidpEntries_DocumentId",
                table: "MidpEntries",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_MidpEntries_OwnerId",
                table: "MidpEntries",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_MidpEntries_ProjectId_DueDate",
                table: "MidpEntries",
                columns: new[] { "ProjectId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TidpEntries_MidpEntryId_TeamName",
                table: "TidpEntries",
                columns: new[] { "MidpEntryId", "TeamName" });

            migrationBuilder.CreateIndex(
                name: "IX_TidpEntries_ProjectId_DueDate",
                table: "TidpEntries",
                columns: new[] { "ProjectId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TidpEntries_SignedOffById",
                table: "TidpEntries",
                column: "SignedOffById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TidpEntries");

            migrationBuilder.DropTable(
                name: "MidpEntries");
        }
    }
}
