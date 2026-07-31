using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkadeHeroes.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppendOnlyAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActorPlayerId = table.Column<string>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "AuditEventSubjects",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEventSubjects", x => new { x.Sequence, x.SubjectId });
                    table.ForeignKey(
                        name: "FK_AuditEventSubjects_AuditEvents_Sequence",
                        column: x => x.Sequence,
                        principalTable: "AuditEvents",
                        principalColumn: "Sequence",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorPlayerId",
                table: "AuditEvents",
                column: "ActorPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_DedupKey",
                table: "AuditEvents",
                column: "DedupKey",
                unique: true,
                filter: "\"DedupKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventType",
                table: "AuditEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEventSubjects_SubjectId",
                table: "AuditEventSubjects",
                column: "SubjectId");

            // APPEND-ONLY, ENFORCED BY THE DATABASE — not by the absence of an update method in C#.
            //
            // "Immutable" that rests on nobody writing the wrong code is a convention, and a convention is
            // exactly what an audit log cannot be: the whole value of the record is that it still says what
            // it said at the time. These triggers make UPDATE and DELETE on the log fail loudly for EVERY
            // writer — the application, a future migration, a hand-run sqlite3 session, a compromised
            // process that still has the file — rather than only for the ones that remembered.
            //
            // The cost is deliberate and worth naming: a later migration that needs to reshape these tables
            // must DROP the trigger first, which is a visible, reviewable line in a diff rather than a
            // silent rewrite of history. DROP TABLE still works (it takes its triggers with it), so a clean
            // teardown is unaffected.
            foreach (var table in new[] { "AuditEvents", "AuditEventSubjects" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_AppendOnly_NoUpdate
                    BEFORE UPDATE ON {table}
                    BEGIN
                        SELECT RAISE(ABORT, '{table} is append-only: a recorded event may never be updated.');
                    END;
                    """);
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_AppendOnly_NoDelete
                    BEFORE DELETE ON {table}
                    BEGIN
                        SELECT RAISE(ABORT, '{table} is append-only: a recorded event may never be deleted.');
                    END;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Triggers first: dropping them is the explicit act of taking the append-only guarantee off,
            // and it must not be something a DropTable does by accident on the way past.
            foreach (var table in new[] { "AuditEvents", "AuditEventSubjects" })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {table}_AppendOnly_NoUpdate;");
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {table}_AppendOnly_NoDelete;");
            }

            migrationBuilder.DropTable(
                name: "AuditEventSubjects");

            migrationBuilder.DropTable(
                name: "AuditEvents");
        }
    }
}
