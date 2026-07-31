using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HeroSaleHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HeroSales",
                columns: table => new
                {
                    OfferId = table.Column<string>(type: "TEXT", nullable: false),
                    HeroId = table.Column<string>(type: "TEXT", nullable: false),
                    SellerId = table.Column<string>(type: "TEXT", nullable: false),
                    BuyerId = table.Column<string>(type: "TEXT", nullable: true),
                    AskSats = table.Column<long>(type: "INTEGER", nullable: false),
                    ListingFeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    SoldAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeroSales", x => x.OfferId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HeroSales");
        }
    }
}
