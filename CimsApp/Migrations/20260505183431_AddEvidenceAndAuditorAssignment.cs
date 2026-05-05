using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceAndAuditorAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditorProjectAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeAreas = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditorProjectAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditorProjectAssignments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AuditorProjectAssignments_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AuditorProjectAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EvidenceArtefacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComplianceArea = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RfiId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuditLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InspectionActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AddedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceArtefacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceArtefacts_AuditLogs_AuditLogId",
                        column: x => x.AuditLogId,
                        principalTable: "AuditLogs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvidenceArtefacts_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvidenceArtefacts_InspectionActivities_InspectionActivityId",
                        column: x => x.InspectionActivityId,
                        principalTable: "InspectionActivities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvidenceArtefacts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvidenceArtefacts_Rfis_RfiId",
                        column: x => x.RfiId,
                        principalTable: "Rfis",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvidenceArtefacts_Users_AddedById",
                        column: x => x.AddedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditorProjectAssignments_CreatedById",
                table: "AuditorProjectAssignments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_AuditorProjectAssignments_ProjectId_ExpiresAt",
                table: "AuditorProjectAssignments",
                columns: new[] { "ProjectId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditorProjectAssignments_UserId_ProjectId_IsRevoked",
                table: "AuditorProjectAssignments",
                columns: new[] { "UserId", "ProjectId", "IsRevoked" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceArtefacts_AddedById",
                table: "EvidenceArtefacts",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceArtefacts_AuditLogId",
                table: "EvidenceArtefacts",
                column: "AuditLogId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceArtefacts_DocumentId",
                table: "EvidenceArtefacts",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceArtefacts_InspectionActivityId",
                table: "EvidenceArtefacts",
                column: "InspectionActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceArtefacts_ProjectId_ComplianceArea_AddedAt",
                table: "EvidenceArtefacts",
                columns: new[] { "ProjectId", "ComplianceArea", "AddedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceArtefacts_RfiId",
                table: "EvidenceArtefacts",
                column: "RfiId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditorProjectAssignments");

            migrationBuilder.DropTable(
                name: "EvidenceArtefacts");
        }
    }
}
