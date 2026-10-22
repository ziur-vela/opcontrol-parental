using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockReason",
                table: "VisitRecords",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlockedLookups",
                table: "VisitRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Lookups",
                table: "VisitRecords",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RepeatedBlockAlertHours",
                table: "FamilySettings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<bool>(
                name: "EnterToSend",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockReason",
                table: "VisitRecords");

            migrationBuilder.DropColumn(
                name: "BlockedLookups",
                table: "VisitRecords");

            migrationBuilder.DropColumn(
                name: "Lookups",
                table: "VisitRecords");

            migrationBuilder.DropColumn(
                name: "RepeatedBlockAlertHours",
                table: "FamilySettings");

            migrationBuilder.DropColumn(
                name: "EnterToSend",
                table: "AspNetUsers");
        }
    }
}
