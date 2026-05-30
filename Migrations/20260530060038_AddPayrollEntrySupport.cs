using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HazelInvoice.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollEntrySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceRecords_PayrollPeriods_PayrollPeriodId",
                table: "AttendanceRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_PayrollPayments_PayrollPeriods_PayrollPeriodId",
                table: "PayrollPayments");

            migrationBuilder.DropTable(
                name: "PayrollAdjustments");

            migrationBuilder.DropTable(
                name: "PayrollPeriods");

            migrationBuilder.DropColumn(
                name: "Multiplier",
                table: "AttendanceRecords");

            migrationBuilder.RenameColumn(
                name: "PeriodStart",
                table: "PayrollRuns",
                newName: "WeekStart");

            migrationBuilder.RenameColumn(
                name: "PeriodEnd",
                table: "PayrollRuns",
                newName: "WeekEnd");

            migrationBuilder.RenameColumn(
                name: "GeneratedBy",
                table: "PayrollRuns",
                newName: "CreatedBy");

            migrationBuilder.RenameColumn(
                name: "GeneratedAt",
                table: "PayrollRuns",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_PayrollRuns_PeriodStart_PeriodEnd",
                table: "PayrollRuns",
                newName: "IX_PayrollRuns_WeekStart_WeekEnd");

            migrationBuilder.RenameColumn(
                name: "PayrollPeriodId",
                table: "PayrollPayments",
                newName: "PayrollEntryId");

            migrationBuilder.RenameIndex(
                name: "IX_PayrollPayments_PayrollPeriodId",
                table: "PayrollPayments",
                newName: "IX_PayrollPayments_PayrollEntryId");

            migrationBuilder.RenameColumn(
                name: "PayrollPeriodId",
                table: "AttendanceRecords",
                newName: "PayrollEntryId");

            migrationBuilder.RenameIndex(
                name: "IX_AttendanceRecords_PayrollPeriodId",
                table: "AttendanceRecords",
                newName: "IX_AttendanceRecords_PayrollEntryId");

            migrationBuilder.AlterColumn<DateTime>(
                name: "HiredDate",
                table: "Laborers",
                type: "timestamp without time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "AttendanceRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CashAdvances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaborerId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    RemainingBalance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedById = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAdvances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashAdvances_Laborers_LaborerId",
                        column: x => x.LaborerId,
                        principalTable: "Laborers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LaborerSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaborerId = table.Column<int>(type: "integer", nullable: false),
                    WorkDays = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborerSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LaborerSchedules_Laborers_LaborerId",
                        column: x => x.LaborerId,
                        principalTable: "Laborers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayrollEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayrollRunId = table.Column<int>(type: "integer", nullable: false),
                    LaborerId = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TotalDays = table.Column<int>(type: "integer", nullable: false),
                    GrossWage = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalDeductions = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalAdditions = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    NetPay = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollEntries_Laborers_LaborerId",
                        column: x => x.LaborerId,
                        principalTable: "Laborers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PayrollEntries_PayrollRuns_PayrollRunId",
                        column: x => x.PayrollRunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Adjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayrollEntryId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Adjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Adjustments_PayrollEntries_PayrollEntryId",
                        column: x => x.PayrollEntryId,
                        principalTable: "PayrollEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdvanceDeductions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayrollEntryId = table.Column<int>(type: "integer", nullable: false),
                    CashAdvanceId = table.Column<int>(type: "integer", nullable: false),
                    DeductAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvanceDeductions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdvanceDeductions_CashAdvances_CashAdvanceId",
                        column: x => x.CashAdvanceId,
                        principalTable: "CashAdvances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdvanceDeductions_PayrollEntries_PayrollEntryId",
                        column: x => x.PayrollEntryId,
                        principalTable: "PayrollEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Adjustments_Date",
                table: "Adjustments",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Adjustments_PayrollEntryId",
                table: "Adjustments",
                column: "PayrollEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AdvanceDeductions_CashAdvanceId",
                table: "AdvanceDeductions",
                column: "CashAdvanceId");

            migrationBuilder.CreateIndex(
                name: "IX_AdvanceDeductions_PayrollEntryId",
                table: "AdvanceDeductions",
                column: "PayrollEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CashAdvances_LaborerId",
                table: "CashAdvances",
                column: "LaborerId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborerSchedules_LaborerId",
                table: "LaborerSchedules",
                column: "LaborerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollEntries_LaborerId",
                table: "PayrollEntries",
                column: "LaborerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollEntries_PayrollRunId",
                table: "PayrollEntries",
                column: "PayrollRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollEntries_PeriodStart",
                table: "PayrollEntries",
                column: "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollEntries_Status",
                table: "PayrollEntries",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceRecords_PayrollEntries_PayrollEntryId",
                table: "AttendanceRecords",
                column: "PayrollEntryId",
                principalTable: "PayrollEntries",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PayrollPayments_PayrollEntries_PayrollEntryId",
                table: "PayrollPayments",
                column: "PayrollEntryId",
                principalTable: "PayrollEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceRecords_PayrollEntries_PayrollEntryId",
                table: "AttendanceRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_PayrollPayments_PayrollEntries_PayrollEntryId",
                table: "PayrollPayments");

            migrationBuilder.DropTable(
                name: "Adjustments");

            migrationBuilder.DropTable(
                name: "AdvanceDeductions");

            migrationBuilder.DropTable(
                name: "LaborerSchedules");

            migrationBuilder.DropTable(
                name: "CashAdvances");

            migrationBuilder.DropTable(
                name: "PayrollEntries");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AttendanceRecords");

            migrationBuilder.RenameColumn(
                name: "WeekStart",
                table: "PayrollRuns",
                newName: "PeriodStart");

            migrationBuilder.RenameColumn(
                name: "WeekEnd",
                table: "PayrollRuns",
                newName: "PeriodEnd");

            migrationBuilder.RenameColumn(
                name: "CreatedBy",
                table: "PayrollRuns",
                newName: "GeneratedBy");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "PayrollRuns",
                newName: "GeneratedAt");

            migrationBuilder.RenameIndex(
                name: "IX_PayrollRuns_WeekStart_WeekEnd",
                table: "PayrollRuns",
                newName: "IX_PayrollRuns_PeriodStart_PeriodEnd");

            migrationBuilder.RenameColumn(
                name: "PayrollEntryId",
                table: "PayrollPayments",
                newName: "PayrollPeriodId");

            migrationBuilder.RenameIndex(
                name: "IX_PayrollPayments_PayrollEntryId",
                table: "PayrollPayments",
                newName: "IX_PayrollPayments_PayrollPeriodId");

            migrationBuilder.RenameColumn(
                name: "PayrollEntryId",
                table: "AttendanceRecords",
                newName: "PayrollPeriodId");

            migrationBuilder.RenameIndex(
                name: "IX_AttendanceRecords_PayrollEntryId",
                table: "AttendanceRecords",
                newName: "IX_AttendanceRecords_PayrollPeriodId");

            migrationBuilder.AlterColumn<DateTime>(
                name: "HiredDate",
                table: "Laborers",
                type: "timestamp without time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone");

            migrationBuilder.AddColumn<decimal>(
                name: "Multiplier",
                table: "AttendanceRecords",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "PayrollPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaborerId = table.Column<int>(type: "integer", nullable: false),
                    PayrollRunId = table.Column<int>(type: "integer", nullable: true),
                    AdjustmentTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalDays = table.Column<int>(type: "integer", nullable: false),
                    TotalWage = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
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
                    table.ForeignKey(
                        name: "FK_PayrollPeriods_PayrollRuns_PayrollRunId",
                        column: x => x.PayrollRunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayrollAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayrollPeriodId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollAdjustments_PayrollPeriods_PayrollPeriodId",
                        column: x => x.PayrollPeriodId,
                        principalTable: "PayrollPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustments_Date",
                table: "PayrollAdjustments",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustments_PayrollPeriodId",
                table: "PayrollAdjustments",
                column: "PayrollPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_LaborerId",
                table: "PayrollPeriods",
                column: "LaborerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_PayrollRunId",
                table: "PayrollPeriods",
                column: "PayrollRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_PeriodStart",
                table: "PayrollPeriods",
                column: "PeriodStart");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriods_Status",
                table: "PayrollPeriods",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceRecords_PayrollPeriods_PayrollPeriodId",
                table: "AttendanceRecords",
                column: "PayrollPeriodId",
                principalTable: "PayrollPeriods",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PayrollPayments_PayrollPeriods_PayrollPeriodId",
                table: "PayrollPayments",
                column: "PayrollPeriodId",
                principalTable: "PayrollPeriods",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
