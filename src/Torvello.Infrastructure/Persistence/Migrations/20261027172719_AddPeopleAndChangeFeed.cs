using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Torvello.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleAndChangeFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompletedAtMs",
                table: "SyncedOrders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "SyncedOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    BirthdayMonth = table.Column<int>(type: "integer", nullable: true),
                    BirthdayDay = table.Column<int>(type: "integer", nullable: true),
                    AnniversaryMonth = table.Column<int>(type: "integer", nullable: true),
                    AnniversaryDay = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MarketingConsent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    TillCustomerId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerChanges",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntityType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerChanges", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "StaffRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    JoinDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NationalIdProtected = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EmergencyContactName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    EmergencyContactPhone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncedOrders_CustomerId",
                table: "SyncedOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Phone",
                table: "Customers",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffRecords_Email",
                table: "StaffRecords",
                column: "Email");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncedOrders_Customers_CustomerId",
                table: "SyncedOrders",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncedOrders_Customers_CustomerId",
                table: "SyncedOrders");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "ServerChanges");

            migrationBuilder.DropTable(
                name: "StaffRecords");

            migrationBuilder.DropIndex(
                name: "IX_SyncedOrders_CustomerId",
                table: "SyncedOrders");

            migrationBuilder.DropColumn(
                name: "CompletedAtMs",
                table: "SyncedOrders");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "SyncedOrders");
        }
    }
}
