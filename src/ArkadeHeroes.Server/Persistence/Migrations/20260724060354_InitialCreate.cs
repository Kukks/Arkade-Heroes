using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FancyFinds",
                columns: table => new
                {
                    HeroId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    HeroName = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    UnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Edition = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FancyFinds", x => x.HeroId);
                });

            migrationBuilder.CreateTable(
                name: "Heroes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    GenomeHex = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentAId = table.Column<string>(type: "TEXT", nullable: true),
                    ParentBId = table.Column<string>(type: "TEXT", nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Xp = table.Column<long>(type: "INTEGER", nullable: false),
                    BreedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BreedCooldownUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    GauntletCooldownUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EquipmentJson = table.Column<string>(type: "TEXT", nullable: false),
                    EntropyHex = table.Column<string>(type: "TEXT", nullable: true),
                    ServerSeedHex = table.Column<string>(type: "TEXT", nullable: true),
                    PlayerNonce = table.Column<string>(type: "TEXT", nullable: true),
                    AssetId = table.Column<string>(type: "TEXT", nullable: true),
                    MintArkTxId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Heroes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemPurchases",
                columns: table => new
                {
                    InvoiceId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ItemAssetId = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveryTxId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPurchases", x => x.InvoiceId);
                });

            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    StarterClaimed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LoginPubKeyHex = table.Column<string>(type: "TEXT", nullable: true),
                    StreakCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastClaimDay = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tournaments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OpenerPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    BuyInSats = table.Column<long>(type: "INTEGER", nullable: false),
                    Size = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerSeed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentHex = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    EntrantsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tournaments", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FancyFinds");

            migrationBuilder.DropTable(
                name: "Heroes");

            migrationBuilder.DropTable(
                name: "ItemPurchases");

            migrationBuilder.DropTable(
                name: "Players");

            migrationBuilder.DropTable(
                name: "Tournaments");
        }
    }
}
