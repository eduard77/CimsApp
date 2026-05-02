using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLpsBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LookaheadEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeekStarting = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConstraintsRemoved = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookaheadEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LookaheadEntries_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LookaheadEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LookaheadEntries_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WeeklyWorkPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeekStarting = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyWorkPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyWorkPlans_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WeeklyWorkPlans_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WeeklyTaskCommitments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeeklyWorkPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Committed = table.Column<bool>(type: "bit", nullable: false),
                    Completed = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyTaskCommitments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyTaskCommitments_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WeeklyTaskCommitments_WeeklyWorkPlans_WeeklyWorkPlanId",
                        column: x => x.WeeklyWorkPlanId,
                        principalTable: "WeeklyWorkPlans",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LookaheadEntries_ActivityId_WeekStarting",
                table: "LookaheadEntries",
                columns: new[] { "ActivityId", "WeekStarting" });

            migrationBuilder.CreateIndex(
                name: "IX_LookaheadEntries_CreatedById",
                table: "LookaheadEntries",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_LookaheadEntries_ProjectId_WeekStarting_IsActive",
                table: "LookaheadEntries",
                columns: new[] { "ProjectId", "WeekStarting", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyTaskCommitments_ActivityId",
                table: "WeeklyTaskCommitments",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyTaskCommitments_ProjectId_WeeklyWorkPlanId",
                table: "WeeklyTaskCommitments",
                columns: new[] { "ProjectId", "WeeklyWorkPlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyTaskCommitments_WeeklyWorkPlanId_ActivityId",
                table: "WeeklyTaskCommitments",
                columns: new[] { "WeeklyWorkPlanId", "ActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyWorkPlans_CreatedById",
                table: "WeeklyWorkPlans",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyWorkPlans_ProjectId_WeekStarting",
                table: "WeeklyWorkPlans",
                columns: new[] { "ProjectId", "WeekStarting" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LookaheadEntries");

            migrationBuilder.DropTable(
                name: "WeeklyTaskCommitments");

            migrationBuilder.DropTable(
                name: "WeeklyWorkPlans");
        }
    }
}
