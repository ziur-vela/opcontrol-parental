using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job5BMultitenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Families",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTime>(
                name: "SuspendedUtc",
                table: "Families",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsServerAdmin",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"Permissions\" = 127 WHERE \"Permissions\" = 0;");
            migrationBuilder.Sql("UPDATE \"Families\" SET \"Status\" = 'Active' WHERE \"Status\" = '';");

            migrationBuilder.AddColumn<int>(
                name: "EmailAttempts",
                table: "Alerts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmailLastError",
                table: "Alerts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextEmailAttemptUtc",
                table: "Alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertPreferences",
                columns: table => new
                {
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MutedNewApp = table.Column<bool>(type: "boolean", nullable: false),
                    MutedRepeatedBlock = table.Column<bool>(type: "boolean", nullable: false),
                    MutedPinOverride = table.Column<bool>(type: "boolean", nullable: false),
                    MutedLimit = table.Column<bool>(type: "boolean", nullable: false),
                    MutedOffline = table.Column<bool>(type: "boolean", nullable: false),
                    MutedTampering = table.Column<bool>(type: "boolean", nullable: false),
                    QuietStartMinutes = table.Column<int>(type: "integer", nullable: false),
                    QuietEndMinutes = table.Column<int>(type: "integer", nullable: false),
                    DailyDigest = table.Column<bool>(type: "boolean", nullable: false),
                    DigestHour = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertPreferences", x => x.FamilyId);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenProtected = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedByParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedFamilyId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerAdminAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerAdminAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SmtpHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPasswordProtected = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FromName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_IsServerAdmin",
                table: "AspNetUsers",
                column: "IsServerAdmin");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_NextEmailAttemptUtc",
                table: "Alerts",
                column: "NextEmailAttemptUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Email_UsedUtc",
                table: "Invitations",
                columns: new[] { "Email", "UsedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_FamilyId_Kind_CreatedUtc",
                table: "Invitations",
                columns: new[] { "FamilyId", "Kind", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerAdminAudits_CreatedUtc",
                table: "ServerAdminAudits",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertPreferences");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "ServerAdminAudits");

            migrationBuilder.DropTable(
                name: "ServerSettings");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_IsServerAdmin",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_Alerts_NextEmailAttemptUtc",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Families");

            migrationBuilder.DropColumn(
                name: "SuspendedUtc",
                table: "Families");

            migrationBuilder.DropColumn(
                name: "IsServerAdmin",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailAttempts",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "EmailLastError",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "NextEmailAttemptUtc",
                table: "Alerts");
        }
    }
}
