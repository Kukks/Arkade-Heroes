using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordTermsAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TermsAcceptedAtUtc",
                table: "Players",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TermsAcceptedVersion",
                table: "Players",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TermsAcceptedAtUtc",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedVersion",
                table: "Players");
        }
    }
}
