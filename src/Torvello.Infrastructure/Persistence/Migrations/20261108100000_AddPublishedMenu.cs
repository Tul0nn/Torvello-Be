using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Torvello.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishedMenu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishedMenus",
                columns: table => new
                {
                    CafeId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    PublishedAtMs = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedMenus", x => x.CafeId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PublishedMenus");
        }
    }
}
