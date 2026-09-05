using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GauntletSessionDurable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Gauntlets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    HeroId = table.Column<string>(type: "TEXT", nullable: false),
                    ServerSeed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentHex = table.Column<string>(type: "TEXT", nullable: false),
                    FeeInvoiceId = table.Column<string>(type: "TEXT", nullable: false),
                    FeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gauntlets", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Gauntlets");
        }
    }
}
