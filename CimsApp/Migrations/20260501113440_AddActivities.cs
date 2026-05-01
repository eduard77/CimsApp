using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Duration = table.Column<decimal>(type: "decimal(9,2)", nullable: false),
                    DurationUnit = table.Column<int>(type: "int", nullable: false),
                    EarlyStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EarlyFinish = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LateStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LateFinish = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalFloat = table.Column<decimal>(type: "decimal(9,2)", nullable: true),
                    FreeFloat = table.Column<decimal>(type: "decimal(9,2)", nullable: true),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScheduledFinish = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualFinish = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConstraintType = table.Column<int>(type: "int", nullable: false),
                    ConstraintDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PercentComplete = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Discipline = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activities_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Activities_Users_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Dependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PredecessorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuccessorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Lag = table.Column<decimal>(type: "decimal(9,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dependencies_Activities_PredecessorId",
                        column: x => x.PredecessorId,
                        principalTable: "Activities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Dependencies_Activities_SuccessorId",
                        column: x => x.SuccessorId,
                        principalTable: "Activities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Dependencies_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_AssigneeId",
                table: "Activities",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ProjectId_Code",
                table: "Activities",
                columns: new[] { "ProjectId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ProjectId_IsActive",
                table: "Activities",
                columns: new[] { "ProjectId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Dependencies_PredecessorId_SuccessorId",
                table: "Dependencies",
                columns: new[] { "PredecessorId", "SuccessorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dependencies_ProjectId_PredecessorId",
                table: "Dependencies",
                columns: new[] { "ProjectId", "PredecessorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Dependencies_ProjectId_SuccessorId",
                table: "Dependencies",
                columns: new[] { "ProjectId", "SuccessorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Dependencies_SuccessorId",
                table: "Dependencies",
                column: "SuccessorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dependencies");

            migrationBuilder.DropTable(
                name: "Activities");
        }
    }
}
