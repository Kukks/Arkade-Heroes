using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeathMatchSessionDurable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeathMatches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengerPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    DefenderPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengerHeroId = table.Column<string>(type: "TEXT", nullable: false),
                    DefenderHeroId = table.Column<string>(type: "TEXT", nullable: false),
                    ServerSeed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentHex = table.Column<string>(type: "TEXT", nullable: false),
                    JointEscrowAddress = table.Column<string>(type: "TEXT", nullable: true),
                    ChallengerGearJson = table.Column<string>(type: "TEXT", nullable: false),
                    DefenderGearJson = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengerFeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    DefenderFeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Absorb = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpeciesId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeathMatches", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeathMatches");
        }
    }
}
