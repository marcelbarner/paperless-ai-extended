using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperlessAI.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceType = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelOverride = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviderConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiProviderConfigs_AiConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "AiConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiConnections_Name",
                table: "AiConnections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderConfigs_ConnectionId",
                table: "AiProviderConfigs",
                column: "ConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviderConfigs");

            migrationBuilder.DropTable(
                name: "AiConnections");
        }
    }
}
