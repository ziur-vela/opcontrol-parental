using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job7NoticeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NoticeShown",
                table: "RemoteSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CameraNoticeEnabled",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasCameraLightOrShutter",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ScreenNoticeEnabled",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing devices keep their current behaviour: notices off, camera light/shutter known.
            migrationBuilder.Sql("UPDATE \"Devices\" SET \"ScreenNoticeEnabled\" = false, \"CameraNoticeEnabled\" = false, \"HasCameraLightOrShutter\" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NoticeShown",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "CameraNoticeEnabled",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HasCameraLightOrShutter",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ScreenNoticeEnabled",
                table: "Devices");
        }
    }
}
