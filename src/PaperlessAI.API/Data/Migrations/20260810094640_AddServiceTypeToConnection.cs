using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperlessAI.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceTypeToConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ServiceType",
                table: "AiConnections",
                type: "TEXT",
                nullable: false,
                defaultValue: "OpenAI");

            // Bestehende Verbindungen anhand ihrer Provider-Configs typisieren
            migrationBuilder.Sql(@"
                UPDATE AiConnections
                SET ServiceType = 'DocumentIntelligence'
                WHERE Id IN (
                    SELECT DISTINCT ConnectionId
                    FROM AiProviderConfigs
                    WHERE ServiceType = 'DocumentIntelligence'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServiceType",
                table: "AiConnections");
        }
    }
}
