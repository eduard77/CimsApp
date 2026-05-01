using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEngagementLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EngagementLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StakeholderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActionsAgreed = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecordedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngagementLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngagementLogs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EngagementLogs_Stakeholders_StakeholderId",
                        column: x => x.StakeholderId,
                        principalTable: "Stakeholders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EngagementLogs_Users_RecordedById",
                        column: x => x.RecordedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EngagementLogs_ProjectId_OccurredAt",
                table: "EngagementLogs",
                columns: new[] { "ProjectId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EngagementLogs_RecordedById",
                table: "EngagementLogs",
                column: "RecordedById");

            migrationBuilder.CreateIndex(
                name: "IX_EngagementLogs_StakeholderId_OccurredAt",
                table: "EngagementLogs",
                columns: new[] { "StakeholderId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EngagementLogs");
        }
    }
}
