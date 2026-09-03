using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployToolkit.Core.EfCore.Migrations
{
    /// <inheritdoc />
    public partial class AddApiSettingsAndCredentialLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiCredentialLogs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCredentialLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiSettings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentialLogs_Username_CreatedUtc",
                table: "ApiCredentialLogs",
                columns: new[] { "Username", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiCredentialLogs");

            migrationBuilder.DropTable(
                name: "ApiSettings");
        }
    }
}
