using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job5AInstaller : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InstallLinkMinutes",
                table: "FamilySettings",
                type: "integer",
                nullable: false,
                defaultValue: 1440);

            migrationBuilder.Sql("UPDATE \"FamilySettings\" SET \"InstallLinkMinutes\" = 1440 WHERE \"InstallLinkMinutes\" = 0;");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdateCheckUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousClientVersion",
                table: "Devices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdateRequestedUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateStatus",
                table: "Devices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdateStatusUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateTargetVersion",
                table: "Devices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceUpdateEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceUpdateEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstallLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeProtected = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedByParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedDeviceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UnaccountedIntervals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnaccountedIntervals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUpdateEvents_DeviceId_CreatedUtc",
                table: "DeviceUpdateEvents",
                columns: new[] { "DeviceId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InstallLinks_CodeHash",
                table: "InstallLinks",
                column: "CodeHash");

            migrationBuilder.CreateIndex(
                name: "IX_InstallLinks_FamilyId_ChildId_CreatedUtc",
                table: "InstallLinks",
                columns: new[] { "FamilyId", "ChildId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InstallLinks_TokenHash",
                table: "InstallLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnaccountedIntervals_DeviceId_StartUtc",
                table: "UnaccountedIntervals",
                columns: new[] { "DeviceId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UnaccountedIntervals_FamilyId_ChildId_StartUtc",
                table: "UnaccountedIntervals",
                columns: new[] { "FamilyId", "ChildId", "StartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceUpdateEvents");

            migrationBuilder.DropTable(
                name: "InstallLinks");

            migrationBuilder.DropTable(
                name: "UnaccountedIntervals");

            migrationBuilder.DropColumn(
                name: "InstallLinkMinutes",
                table: "FamilySettings");

            migrationBuilder.DropColumn(
                name: "LastUpdateCheckUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PreviousClientVersion",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UpdateRequestedUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UpdateStatus",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UpdateStatusUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UpdateTargetVersion",
                table: "Devices");
        }
    }
}
