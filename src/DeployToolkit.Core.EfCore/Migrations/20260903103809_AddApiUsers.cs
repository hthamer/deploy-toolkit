using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployToolkit.Core.EfCore.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the <c>ApiUsers</c> table — REST API credentials for
    /// <c>DeployToolkit.Api</c> (Phase 1: the authenticate endpoint).
    /// Passwords are stored as versioned PBKDF2-SHA256 hash strings, never
    /// plaintext. Plain <c>CREATE TABLE</c> — applies cleanly to an existing
    /// registry; the Packager/Deployer ignore the new table.
    /// </summary>
    public partial class AddApiUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiUsers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiUsers", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiUsers_Username",
                table: "ApiUsers",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiUsers");
        }
    }
}
