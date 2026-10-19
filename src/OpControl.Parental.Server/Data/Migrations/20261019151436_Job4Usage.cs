using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job4Usage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AlwaysAllowed",
                table: "AppRules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DailyLimitMinutes",
                table: "AppRules",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientTamperEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientTamperEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FamilySettings",
                columns: table => new
                {
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertEmailsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPasswordProtected = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OfflineAlertMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilySettings", x => x.FamilyId);
                });

            migrationBuilder.CreateTable(
                name: "ScreenTimeLimits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SpokenWarnings = table.Column<bool>(type: "boolean", nullable: false),
                    MondayMinutes = table.Column<int>(type: "integer", nullable: false),
                    TuesdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    WednesdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    ThursdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    FridayMinutes = table.Column<int>(type: "integer", nullable: false),
                    SaturdayMinutes = table.Column<int>(type: "integer", nullable: false),
                    SundayMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenTimeLimits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageIntervals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppIdentity = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AppDisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageIntervals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebsiteUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ChildId_ReadUtc",
                table: "Alerts",
                columns: new[] { "ChildId", "ReadUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ChildId_Type_CreatedUtc",
                table: "Alerts",
                columns: new[] { "ChildId", "Type", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_EmailedUtc",
                table: "Alerts",
                column: "EmailedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_FamilyId_CreatedUtc",
                table: "Alerts",
                columns: new[] { "FamilyId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientTamperEvents_FamilyId_ChildId_OccurredUtc",
                table: "ClientTamperEvents",
                columns: new[] { "FamilyId", "ChildId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScreenTimeLimits_ChildId",
                table: "ScreenTimeLimits",
                column: "ChildId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScreenTimeLimits_FamilyId",
                table: "ScreenTimeLimits",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageIntervals_ChildId_Active_StartUtc",
                table: "UsageIntervals",
                columns: new[] { "ChildId", "Active", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageIntervals_DeviceId_StartUtc",
                table: "UsageIntervals",
                columns: new[] { "DeviceId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageIntervals_FamilyId_ChildId_StartUtc",
                table: "UsageIntervals",
                columns: new[] { "FamilyId", "ChildId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteUsages_DeviceId_StartUtc",
                table: "WebsiteUsages",
                columns: new[] { "DeviceId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteUsages_FamilyId_ChildId_StartUtc",
                table: "WebsiteUsages",
                columns: new[] { "FamilyId", "ChildId", "StartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "ClientTamperEvents");

            migrationBuilder.DropTable(
                name: "FamilySettings");

            migrationBuilder.DropTable(
                name: "ScreenTimeLimits");

            migrationBuilder.DropTable(
                name: "UsageIntervals");

            migrationBuilder.DropTable(
                name: "WebsiteUsages");

            migrationBuilder.DropColumn(
                name: "AlwaysAllowed",
                table: "AppRules");

            migrationBuilder.DropColumn(
                name: "DailyLimitMinutes",
                table: "AppRules");
        }
    }
}
