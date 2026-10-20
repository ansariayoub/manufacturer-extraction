using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturerExtraction.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers");

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsAdmin = table.Column<bool>(type: "bit", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUsers_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Clients",
                columns: new[] { "Id", "CreatedDate", "Name" },
                values: new object[] { new Guid("00000000-0000-0000-0000-00000000c001"), new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Utc), "Default client" });

            // Everything that existed before multi-client support belongs to the default client, so the
            // new columns default to it. The default is deliberately kept: the previously deployed API
            // still inserts documents and manufacturers without knowing about ClientId, and until the
            // new version is live those inserts must keep working (they land on the default client).
            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "Manufacturers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.CreateIndex(
                name: "IX_Manufacturers_ClientId_Name",
                table: "Manufacturers",
                columns: new[] { "ClientId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ClientId",
                table: "Documents",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_ClientId",
                table: "AppUsers",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Email",
                table: "AppUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Clients_ClientId",
                table: "Documents",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Manufacturers_Clients_ClientId",
                table: "Manufacturers",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Clients_ClientId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_Manufacturers_Clients_ClientId",
                table: "Manufacturers");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Manufacturers_ClientId_Name",
                table: "Manufacturers");

            migrationBuilder.DropIndex(
                name: "IX_Documents_ClientId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Manufacturers");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers",
                column: "Name",
                unique: true);
        }
    }
}
