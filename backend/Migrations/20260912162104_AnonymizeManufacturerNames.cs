using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturerExtraction.Api.Migrations
{
    /// <summary>
    /// Replaces the real manufacturer catalog with 15 fictitious placeholder names, requested to
    /// anonymize the app for screenshots/demo material. Deliberately a full replace, not a rename
    /// of each existing row 1:1 — there is no meaningful correspondence between ~97 real names and
    /// 15 fictitious ones, so every Manufacturers row (and its prompt history, cascade-deleted with
    /// it) is dropped and replaced by the 15 new ones. Every Document.Manufacturer value (a plain
    /// string snapshot, not a foreign key — see Manufacturer.cs) is remapped too, deterministically
    /// cycling each DISTINCT real name that appears on a document to one of the 15 fictitious names
    /// in alphabetical order, so the same real manufacturer always lands on the same fictitious one
    /// throughout the existing queue.
    /// </summary>
    public partial class AnonymizeManufacturerNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Prompt history is cascade-deleted with its manufacturer (see AppDbContext), so this
            // alone clears both tables.
            migrationBuilder.Sql("DELETE FROM [Manufacturers];");

            var names = new[]
            {
                "Apex Electronic Solutions", "Catalyst Electronics", "Cornerstone Technical Sales",
                "Delta Industrial Electronics", "Helix Manufacturing Group", "Infinity Tech Reps",
                "Keystone Component Sales", "Meridian Components Corp", "Northgate Technologies",
                "Orion Connectivity Solutions", "Pinnacle Tech Sales", "Stratus Industrial Sales",
                "Summit Power Electronics", "Vortex Power Systems", "Zenith Component Systems",
            };

            var now = DateTime.UtcNow;
            var rows = new object[names.Length, 4];
            for (int i = 0; i < names.Length; i++)
            {
                rows[i, 0] = Guid.NewGuid();
                rows[i, 1] = names[i];
                rows[i, 2] = null;
                rows[i, 3] = now;
            }

            migrationBuilder.InsertData(
                table: "Manufacturers",
                columns: new[] { "Id", "Name", "DefaultInstructions", "CreatedDate" },
                values: rows);

            // Deterministically cycle each distinct real manufacturer name found on existing
            // documents to one of the 15 fictitious names (ordered alphabetically), so every
            // document from the same real manufacturer ends up under the same fictitious one.
            migrationBuilder.Sql(@"
;WITH DistinctMfg AS (
    SELECT DISTINCT [Manufacturer], ROW_NUMBER() OVER (ORDER BY [Manufacturer]) AS rn
    FROM [Documents]
    WHERE [Manufacturer] IS NOT NULL AND [Manufacturer] <> ''
),
Mapped AS (
    SELECT [Manufacturer] AS OldName,
        CASE (rn - 1) % 15
            WHEN 0 THEN N'Apex Electronic Solutions'
            WHEN 1 THEN N'Catalyst Electronics'
            WHEN 2 THEN N'Cornerstone Technical Sales'
            WHEN 3 THEN N'Delta Industrial Electronics'
            WHEN 4 THEN N'Helix Manufacturing Group'
            WHEN 5 THEN N'Infinity Tech Reps'
            WHEN 6 THEN N'Keystone Component Sales'
            WHEN 7 THEN N'Meridian Components Corp'
            WHEN 8 THEN N'Northgate Technologies'
            WHEN 9 THEN N'Orion Connectivity Solutions'
            WHEN 10 THEN N'Pinnacle Tech Sales'
            WHEN 11 THEN N'Stratus Industrial Sales'
            WHEN 12 THEN N'Summit Power Electronics'
            WHEN 13 THEN N'Vortex Power Systems'
            ELSE N'Zenith Component Systems'
        END AS NewName
    FROM DistinctMfg
)
UPDATE d
SET d.[Manufacturer] = m.NewName
FROM [Documents] d
JOIN Mapped m ON d.[Manufacturer] = m.OldName;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: the real manufacturer catalog and the original Document.Manufacturer
            // values are gone once this runs. Restoring from a database backup is the only way
            // back if this migration needs to be undone.
        }
    }
}
