using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Torvello.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ClockInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClockOutAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClockInMs = table.Column<long>(type: "bigint", nullable: false),
                    ClosedBy = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Body = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    Published = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicyAcknowledgements",
                columns: table => new
                {
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyAcknowledgements", x => new { x.PolicyId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PolicyAcknowledgements_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyAcknowledgements_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_BusinessDate",
                table: "AttendanceRecords",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_EmployeeEmail",
                table: "AttendanceRecords",
                column: "EmployeeEmail");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAcknowledgements_UserId",
                table: "PolicyAcknowledgements",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "PolicyAcknowledgements");

            migrationBuilder.DropTable(
                name: "Policies");
        }
    }
}
