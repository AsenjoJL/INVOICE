using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HazelInvoice.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Laborers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FullName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DailyRate = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Role = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ContactNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HiredDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Laborers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaborerId = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TotalDays = table.Column<int>(type: "integer", nullable: false),
                    TotalWage = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollPeriods_Laborers_LaborerId",
                        column: x => x.LaborerId,
                        principalTable: "Laborers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaborerId = table.Column<int>(type: "integer", nullable: false),
                    WorkDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RateSnapshot = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Multiplier = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    WageAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PayrollPeriodId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedById = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Laborers_LaborerId",
                        column: x => x.LaborerId,
                        principalTable: "Laborers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_PayrollPeriods_PayrollPeriodId",
                        column: x => x.PayrollPeriodId,
                        principalTable: "PayrollPeriods",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayrollPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayrollPeriodId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentMethod = table.Column<int>(type: "integer", nullable: false),
                    ReferenceNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RecordedById = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollPayments_PayrollPeriods_PayrollPeriodId",
                        column: x => x.PayrollPeriodId,
                        principalTable: "PayrollPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_LaborerId_WorkDate",
                table: "AttendanceRecords",
                columns: new[] { "LaborerId", "WorkDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_PayrollPeriodId",
                table: "AttendanceRecords",
                column: "PayrollPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_WorkDate",
                table: "AttendanceRecords",
                column: "WorkDate");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPayments_PayrollPeriodId",
                table: "PayrollPayments",
                column: "PayrollPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_LaborerId",
                table: "PayrollPeriods",
                column: "LaborerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_PeriodStart",
                table: "PayrollPeriods",
                column: "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_Status",
                table: "PayrollPeriods",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "PayrollPayments");

            migrationBuilder.DropTable(
                name: "PayrollPeriods");

            migrationBuilder.DropTable(
                name: "Laborers");
        }
    }
}
