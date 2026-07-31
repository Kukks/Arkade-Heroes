using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StudProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudProposals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProposerPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    StudOwnerPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    ProposerHeroId = table.Column<string>(type: "TEXT", nullable: false),
                    StudHeroId = table.Column<string>(type: "TEXT", nullable: false),
                    ServerSeed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentHex = table.Column<string>(type: "TEXT", nullable: false),
                    StudFeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    BreedFeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    BreedFeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    StudFeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Declined = table.Column<bool>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StudFeePaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChildHeroId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudProposals", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudProposals");
        }
    }
}
