using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CimsApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CertificateNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CumulativeValuation = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CumulativeMaterialsOnSite = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RetentionPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    IncludedVariationsAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    IssuedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentCertificates_CostPeriods_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "CostPeriods",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PaymentCertificates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PaymentCertificates_Users_IssuedById",
                        column: x => x.IssuedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCertificates_IssuedById",
                table: "PaymentCertificates",
                column: "IssuedById");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCertificates_PeriodId",
                table: "PaymentCertificates",
                column: "PeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCertificates_ProjectId_CertificateNumber",
                table: "PaymentCertificates",
                columns: new[] { "ProjectId", "CertificateNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCertificates_ProjectId_PeriodId",
                table: "PaymentCertificates",
                columns: new[] { "ProjectId", "PeriodId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentCertificates");
        }
    }
}
