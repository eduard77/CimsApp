using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduleBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivitiesCount = table.Column<int>(type: "int", nullable: false),
                    ProjectFinishAtBaseline = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleBaselines_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ScheduleBaselines_Users_CapturedById",
                        column: x => x.CapturedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ScheduleBaselineActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleBaselineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Duration = table.Column<decimal>(type: "decimal(9,2)", nullable: false),
                    EarlyStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EarlyFinish = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleBaselineActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleBaselineActivities_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ScheduleBaselineActivities_ScheduleBaselines_ScheduleBaselineId",
                        column: x => x.ScheduleBaselineId,
                        principalTable: "ScheduleBaselines",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleBaselineActivities_ActivityId",
                table: "ScheduleBaselineActivities",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleBaselineActivities_ScheduleBaselineId_ActivityId",
                table: "ScheduleBaselineActivities",
                columns: new[] { "ScheduleBaselineId", "ActivityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleBaselines_CapturedById",
                table: "ScheduleBaselines",
                column: "CapturedById");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleBaselines_ProjectId_CapturedAt",
                table: "ScheduleBaselines",
                columns: new[] { "ProjectId", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleBaselineActivities");

            migrationBuilder.DropTable(
                name: "ScheduleBaselines");
        }
    }
}
