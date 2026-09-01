using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployToolkit.Core.EfCore.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    ClientId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.ClientId);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentComponents",
                columns: table => new
                {
                    ComponentId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetFramework = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsSelfContained = table.Column<bool>(type: "bit", nullable: false),
                    IisSiteName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IisAppPath = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AzureAppServiceName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AzureResourceGroup = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PleskHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PleskSiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    HealthCheckUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    DbConnectionRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentComponents", x => x.ComponentId);
                    table.ForeignKey(
                        name: "FK_DeploymentComponents_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "ClientId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    PackageId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ComponentId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GitCommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DeployedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeployedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.PackageId);
                    table.ForeignKey(
                        name: "FK_Packages_DeploymentComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "DeploymentComponents",
                        principalColumn: "ComponentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentRuns",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PackageId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LogPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    HealthCheckResult = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentRuns", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_DeploymentRuns_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "PackageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentComponents_ClientId_Name",
                table: "DeploymentComponents",
                columns: new[] { "ClientId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRuns_PackageId",
                table: "DeploymentRuns",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ComponentId_CreatedUtc",
                table: "Packages",
                columns: new[] { "ComponentId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ComponentId_Status",
                table: "Packages",
                columns: new[] { "ComponentId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentRuns");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropTable(
                name: "DeploymentComponents");

            migrationBuilder.DropTable(
                name: "Clients");
        }
    }
}
