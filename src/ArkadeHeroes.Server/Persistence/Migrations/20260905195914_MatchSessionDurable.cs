using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MatchSessionDurable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengerPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    DefenderPlayerId = table.Column<string>(type: "TEXT", nullable: true),
                    ChallengerLineupJson = table.Column<string>(type: "TEXT", nullable: false),
                    DefenderLineupJson = table.Column<string>(type: "TEXT", nullable: false),
                    ServerSeed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentHex = table.Column<string>(type: "TEXT", nullable: false),
                    WagerSats = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    EscrowChallengerAddress = table.Column<string>(type: "TEXT", nullable: true),
                    EscrowDefenderAddress = table.Column<string>(type: "TEXT", nullable: true),
                    RefundAfterUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ChallengerInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    DefenderInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    ChallengerFeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    DefenderFeeInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchSessions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchSessions");
        }
    }
}
