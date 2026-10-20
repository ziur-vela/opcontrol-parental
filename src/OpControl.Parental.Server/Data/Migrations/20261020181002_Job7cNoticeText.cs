using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job7cNoticeText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NoticeText",
                table: "FamilySettings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "Parent's watching you");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NoticeText",
                table: "FamilySettings");
        }
    }
}
