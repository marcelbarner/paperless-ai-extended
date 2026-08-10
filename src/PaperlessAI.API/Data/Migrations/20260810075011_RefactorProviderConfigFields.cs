using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperlessAI.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefactorProviderConfigFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ModelOverride",
                table: "AiProviderConfigs",
                newName: "OutputFormat");

            migrationBuilder.AddColumn<string>(
                name: "AnalysisModel",
                table: "AiProviderConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeploymentName",
                table: "AiProviderConfigs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisModel",
                table: "AiProviderConfigs");

            migrationBuilder.DropColumn(
                name: "DeploymentName",
                table: "AiProviderConfigs");

            migrationBuilder.RenameColumn(
                name: "OutputFormat",
                table: "AiProviderConfigs",
                newName: "ModelOverride");
        }
    }
}
