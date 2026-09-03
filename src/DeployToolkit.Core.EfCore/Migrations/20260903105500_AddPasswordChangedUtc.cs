using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployToolkit.Core.EfCore.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the nullable <c>PasswordChangedUtc</c> column to <c>ApiUsers</c> —
    /// audit stamp for the API's background password-rotation service
    /// (Auth:PasswordRotation, default every 45 minutes). Plain safe
    /// <c>ALTER TABLE … ADD</c>; existing rows keep null until their first
    /// rotation/login-path write.
    /// </summary>
    public partial class AddPasswordChangedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PasswordChangedUtc",
                table: "ApiUsers",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordChangedUtc",
                table: "ApiUsers");
        }
    }
}
