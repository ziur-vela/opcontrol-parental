using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job2Lock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BonusMinutes",
                table: "LockStates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverridePinHash",
                table: "LockStates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientVersion",
                table: "Devices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BonusMinutes",
                table: "LockStates");

            migrationBuilder.DropColumn(
                name: "OverridePinHash",
                table: "LockStates");

            migrationBuilder.DropColumn(
                name: "ClientVersion",
                table: "Devices");
        }
    }
}
