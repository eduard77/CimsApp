using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddGdpr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataBreachLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DiscoveredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DataCategoriesCsv = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AffectedSubjectsCount = table.Column<int>(type: "int", nullable: true),
                    ReportedToIco = table.Column<bool>(type: "bit", nullable: false),
                    ReportedToIcoAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IcoReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NotifiedDataSubjects = table.Column<bool>(type: "bit", nullable: false),
                    NotifiedDataSubjectsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataBreachLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataBreachLogs_Organisations_OrganisationId",
                        column: x => x.OrganisationId,
                        principalTable: "Organisations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataBreachLogs_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Dpias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HighRiskProcessingDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MitigationsDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dpias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dpias_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Dpias_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Dpias_Users_ReviewedById",
                        column: x => x.ReviewedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RetentionSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataCategory = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RetentionPeriodMonths = table.Column<int>(type: "int", nullable: false),
                    LawfulBasisForRetention = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RetentionSchedules_Organisations_OrganisationId",
                        column: x => x.OrganisationId,
                        principalTable: "Organisations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RopaEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LawfulBasis = table.Column<int>(type: "int", nullable: false),
                    DataCategoriesCsv = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetentionPeriod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecurityMeasures = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RopaEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RopaEntries_Organisations_OrganisationId",
                        column: x => x.OrganisationId,
                        principalTable: "Organisations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SubjectAccessRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DataSubjectName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DataSubjectEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequestDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FulfilledById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FulfilledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FulfilmentNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RefusedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RefusedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefusalReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubjectAccessRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubjectAccessRequests_Organisations_OrganisationId",
                        column: x => x.OrganisationId,
                        principalTable: "Organisations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubjectAccessRequests_Users_FulfilledById",
                        column: x => x.FulfilledById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubjectAccessRequests_Users_RefusedById",
                        column: x => x.RefusedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataBreachLogs_CreatedById",
                table: "DataBreachLogs",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_DataBreachLogs_OrganisationId_DiscoveredAt",
                table: "DataBreachLogs",
                columns: new[] { "OrganisationId", "DiscoveredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DataBreachLogs_OrganisationId_Number",
                table: "DataBreachLogs",
                columns: new[] { "OrganisationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dpias_CreatedById",
                table: "Dpias",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Dpias_ProjectId_State",
                table: "Dpias",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Dpias_ReviewedById",
                table: "Dpias",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionSchedules_OrganisationId_DataCategory",
                table: "RetentionSchedules",
                columns: new[] { "OrganisationId", "DataCategory" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RopaEntries_OrganisationId_IsActive",
                table: "RopaEntries",
                columns: new[] { "OrganisationId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_SubjectAccessRequests_FulfilledById",
                table: "SubjectAccessRequests",
                column: "FulfilledById");

            migrationBuilder.CreateIndex(
                name: "IX_SubjectAccessRequests_OrganisationId_Number",
                table: "SubjectAccessRequests",
                columns: new[] { "OrganisationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubjectAccessRequests_OrganisationId_State",
                table: "SubjectAccessRequests",
                columns: new[] { "OrganisationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_SubjectAccessRequests_RefusedById",
                table: "SubjectAccessRequests",
                column: "RefusedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataBreachLogs");

            migrationBuilder.DropTable(
                name: "Dpias");

            migrationBuilder.DropTable(
                name: "RetentionSchedules");

            migrationBuilder.DropTable(
                name: "RopaEntries");

            migrationBuilder.DropTable(
                name: "SubjectAccessRequests");
        }
    }
}
