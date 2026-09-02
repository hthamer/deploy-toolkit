using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployToolkit.Core.EfCore.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the <c>PackageLocation</c> column to <c>Packages</c> — where the
    /// built delta.zip physically lives (Option B: shared folder + registry
    /// links the package). Null when no package store is configured (the .zip
    /// lives only on the builder's PC and must be copied to the deployer by
    /// hand — the pre-Option-B behavior).
    /// </summary>
    public partial class AddPackageLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PackageLocation",
                table: "Packages",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PackageLocation",
                table: "Packages");
        }
    }
}
