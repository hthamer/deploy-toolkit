using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployToolkit.Core.EfCore.Migrations
{
    /// <inheritdoc />
    public partial class AddClientProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AmcExpiryDate",
                table: "Clients",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Clients",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPhone",
                table: "Clients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeploymentBranch",
                table: "Clients",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitRepositoryUrl",
                table: "Clients",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasAmc",
                table: "Clients",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HostingAccountManagedBy",
                table: "Clients",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InfrastructureManagedBy",
                table: "Clients",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishConfigurationJson",
                table: "Clients",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmcExpiryDate",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ContactPhone",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "DeploymentBranch",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "GitRepositoryUrl",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "HasAmc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "HostingAccountManagedBy",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "InfrastructureManagedBy",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PublishConfigurationJson",
                table: "Clients");
        }
    }
}
