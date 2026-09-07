using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturerExtraction.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddManufacturers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Manufacturers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultInstructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Manufacturers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers",
                column: "Name",
                unique: true);

            // Seeds the table from the frontend's previously hardcoded MANUFACTURERS array, so
            // switching that dropdown over to this table doesn't lose a single existing name that
            // operators already rely on and existing documents were uploaded under.
            var existingNames = new[]
            {
                "3M Factory JanSan", "Act D'mand Factory", "ALL-AMERICAN METAL CORP. Buy/Sell",
                "Amba Prod Factory", "AMBA PRODUCTS Buy/Sell", "ARMACELL, LLC Buy/Sell",
                "Armstrong Buy/Sell", "ASC Engineered Solutions Factory", "Bemis Factory",
                "Bemis Mfg. Co Buy/Sell", "Bissell", "Bobrick Factory Architectural",
                "Bona Resilient System JanSan", "BrassCraft Buy/Sell", "Brasscraft Factory",
                "BRASSTECH, INC. DBA DELTA FAUCET CO. Buy/Sell", "Brizo Faucet Factory",
                "Cenobots Factory JanSan", "Champion Pump Factory", "Charlotte 3Pl",
                "Charlotte Iron Factory", "Charlotte Plastic Factory", "Chase Products Factory JanSan",
                "CHICAGO FAUCET COMPANY Buy/Sell", "Crown Matting Factory JanSan",
                "Delta Faucet Company Buy/Sell", "Delta Faucet Factory", "DuraVent Buy/Sell",
                "Duravit Factory", "DURAVIT USA, INC Buy/Sell", "Eemax Buy/Sell", "Eemax Factory",
                "Flexcon Industries Buy/Sell", "FLORESTONE LLC Buy/Sell",
                "Frascio Factory Architectural", "GAMCO Buy/Sell", "Gamco Factory Architectural",
                "Geberit Factory", "GREASE GUARDIAN Buy/Sell",
                "GRUNDFOS CBS INC. ACCT NO: 602049828 Buy/Sell", "GRUNDFOS PUMPS CORPORATION Buy/Sell",
                "IBC TECHNOLOGIES USA, INC Buy/Sell", "Ideal Couplings Factory",
                "INFINITY DRAIN Buy/Sell", "Infinity Drain Factory", "Insinkerator Factory",
                "IPS Corporation Buy/Sell", "IPS Factory", "JOHNSON ABRASIVES Buy/Sell",
                "Koala Factory Architectural", "Kraus USA Factory", "KRAUS USA PLUMBING LLC Buy/Sell",
                "Kusel Equipment Buy/Sell", "Kutol Products Company JanSan", "Liberty Factory",
                "Liberty Hardware Buy/Sell", "LOCHINVAR - DO NOT USE Buy/Sell",
                "Mercantile Development, Inc.JanSan", "METPAR CORP Buy/Sell", "Mifab Factory",
                "Mosquito Vac Factory JanSan", "Motor Scrubber Factory JanSan",
                "National Novelty Brush Buy/Sell", "Newport Brass Factory", "NOWSUN Corp. Buy/Sell",
                "Palmer Fixture JanSan", "Precision Plbg Products Factory",
                "PRECISION PLUMBING PRODUCT Buy/Sell", "Pressalit Buy/Sell", "Pyro Shield Buy/Sell",
                "Raypak Buy/Sell", "Raypak Factory", "Raypak Parts Buy/Sell", "Raypak Parts Factory",
                "Rheem Commercial Factory", "Rheem Commerical Buy/Sell", "Rheem Parts Buy/Sell",
                "Rheem Residential Buy/Sell", "Rheem Residential Factory", "Rheem Tankless Buy/Sell",
                "Rheem Tankless Factory", "Rockford Separators Factory", "Symbol Sink Buy/Sell",
                "T-Christy's Factory", "TOTO USA Buy/Sell", "Uponor Factory",
                "UPONOR, INC Buy/Sell", "Vectair JanSan", "Western Pottery Factory",
                "Wheeler Rex Buy/Sell", "Wheeler Rex Factory", "WINSTON WATER COOLER Buy/Sell",
                "WURTH WOOD GROUP Buy/Sell",
            };

            var now = DateTime.UtcNow;
            var rows = new object[existingNames.Length, 4];
            for (int i = 0; i < existingNames.Length; i++)
            {
                rows[i, 0] = Guid.NewGuid();
                rows[i, 1] = existingNames[i];
                rows[i, 2] = null!;
                rows[i, 3] = now;
            }

            migrationBuilder.InsertData(
                table: "Manufacturers",
                columns: new[] { "Id", "Name", "DefaultInstructions", "CreatedDate" },
                values: rows);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Manufacturers");
        }
    }
}
