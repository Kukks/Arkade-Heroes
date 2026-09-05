using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EscrowSessionDurable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EscrowSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstHeroId = table.Column<string>(type: "TEXT", nullable: false),
                    SecondHeroId = table.Column<string>(type: "TEXT", nullable: false),
                    ServerSeed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentHex = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    FeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    EscrowAddress = table.Column<string>(type: "TEXT", nullable: true),
                    FeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscrowSessions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EscrowSessions");
        }
    }
}
