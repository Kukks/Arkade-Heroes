using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordFailedPayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PayoutFailures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    AmountSats = table.Column<long>(type: "INTEGER", nullable: false),
                    PayoutTag = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    Failure = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayoutFailures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayoutFailures_Outcome",
                table: "PayoutFailures",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_PayoutFailures_PlayerId",
                table: "PayoutFailures",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayoutFailures");
        }
    }
}
