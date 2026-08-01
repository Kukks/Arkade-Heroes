using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistTournamentOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigVersion",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentVersion",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntrantSnapshotsJson",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntrantsCommitmentHex",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntropyHex",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nonce",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrizesJson",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigVersion",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "EntrantSnapshotsJson",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "EntrantsCommitmentHex",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "EntropyHex",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "Nonce",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "PrizesJson",
                table: "Tournaments");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "Tournaments");
        }
    }
}
