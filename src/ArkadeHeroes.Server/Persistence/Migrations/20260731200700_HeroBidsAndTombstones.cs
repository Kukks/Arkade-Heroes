using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HeroBidsAndTombstones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HeroBids",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    BidderPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    HeroId = table.Column<string>(type: "TEXT", nullable: false),
                    BidSats = table.Column<long>(type: "INTEGER", nullable: false),
                    FeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    BidInvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Declined = table.Column<bool>(type: "INTEGER", nullable: false),
                    Withdrawn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Settled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Refunded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SellerPaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    RefundPaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReclaimAfterUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeroBids", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HeroTombstones",
                columns: table => new
                {
                    HeroId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    GenomeHex = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    ReplacedByHeroId = table.Column<string>(type: "TEXT", nullable: true),
                    DestroyedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentAId = table.Column<string>(type: "TEXT", nullable: true),
                    ParentBId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeroTombstones", x => x.HeroId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HeroBids_BidderPlayerId",
                table: "HeroBids",
                column: "BidderPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_HeroBids_HeroId",
                table: "HeroBids",
                column: "HeroId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HeroBids");

            migrationBuilder.DropTable(
                name: "HeroTombstones");
        }
    }
}
