using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TreasuryFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TreasuryFlows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", nullable: false),
                    Sats = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryFlows", x => new { x.Direction, x.Id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TreasuryFlows");
        }
    }
}
