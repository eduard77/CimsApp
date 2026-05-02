using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddKaizenLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImprovementRegisterEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CycleNumber = table.Column<int>(type: "int", nullable: false),
                    PlanNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DoNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CheckNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImprovementRegisterEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImprovementRegisterEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ImprovementRegisterEntries_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ImprovementRegisterEntries_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LessonsLearned",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TagsCsv = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonsLearned", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LessonsLearned_Organisations_OrganisationId",
                        column: x => x.OrganisationId,
                        principalTable: "Organisations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LessonsLearned_Projects_SourceProjectId",
                        column: x => x.SourceProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LessonsLearned_Users_RecordedById",
                        column: x => x.RecordedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OpportunitiesToImprove",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceEntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SourceEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaisedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActioned = table.Column<bool>(type: "bit", nullable: false),
                    ActionedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActionedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunitiesToImprove", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunitiesToImprove_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpportunitiesToImprove_Users_RaisedById",
                        column: x => x.RaisedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImprovementRegisterEntries_CreatedById",
                table: "ImprovementRegisterEntries",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ImprovementRegisterEntries_OwnerId",
                table: "ImprovementRegisterEntries",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ImprovementRegisterEntries_ProjectId_Number",
                table: "ImprovementRegisterEntries",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImprovementRegisterEntries_ProjectId_State",
                table: "ImprovementRegisterEntries",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_LessonsLearned_OrganisationId_Category",
                table: "LessonsLearned",
                columns: new[] { "OrganisationId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_LessonsLearned_RecordedById",
                table: "LessonsLearned",
                column: "RecordedById");

            migrationBuilder.CreateIndex(
                name: "IX_LessonsLearned_SourceProjectId",
                table: "LessonsLearned",
                column: "SourceProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitiesToImprove_ProjectId_IsActioned",
                table: "OpportunitiesToImprove",
                columns: new[] { "ProjectId", "IsActioned" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitiesToImprove_ProjectId_Number",
                table: "OpportunitiesToImprove",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitiesToImprove_RaisedById",
                table: "OpportunitiesToImprove",
                column: "RaisedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImprovementRegisterEntries");

            migrationBuilder.DropTable(
                name: "LessonsLearned");

            migrationBuilder.DropTable(
                name: "OpportunitiesToImprove");
        }
    }
}
