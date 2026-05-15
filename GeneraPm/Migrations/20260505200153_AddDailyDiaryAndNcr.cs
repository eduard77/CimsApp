using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeneraPm.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyDiaryAndNcr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyDiaryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiaryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Weather = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Manpower = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Plant = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Deliveries = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Incidents = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ProgressNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyDiaryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyDiaryEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DailyDiaryEntries_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Ncrs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RaisedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedToId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PhotoStorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ncrs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ncrs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Ncrs_Users_AssignedToId",
                        column: x => x.AssignedToId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Ncrs_Users_RaisedById",
                        column: x => x.RaisedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyDiaryEntries_AuthorId",
                table: "DailyDiaryEntries",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyDiaryEntries_ProjectId_DiaryDate",
                table: "DailyDiaryEntries",
                columns: new[] { "ProjectId", "DiaryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyDiaryEntries_ProjectId_DiaryDate_AuthorId",
                table: "DailyDiaryEntries",
                columns: new[] { "ProjectId", "DiaryDate", "AuthorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_AssignedToId",
                table: "Ncrs",
                column: "AssignedToId");

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_ProjectId_Number",
                table: "Ncrs",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_ProjectId_Status",
                table: "Ncrs",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Ncrs_RaisedById",
                table: "Ncrs",
                column: "RaisedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyDiaryEntries");

            migrationBuilder.DropTable(
                name: "Ncrs");
        }
    }
}
