using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job12Updates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GitHubCheckEnabled",
                table: "ServerSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastNotifiedVersion",
                table: "ServerSettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdateCheckUtc",
                table: "ServerSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestManifestUrl",
                table: "ServerSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestNotesEn",
                table: "ServerSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestNotesEs",
                table: "ServerSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestPublishedUtc",
                table: "ServerSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestReleaseUrl",
                table: "ServerSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestVersion",
                table: "ServerSettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyNewVersion",
                table: "ServerSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicBaseUrl",
                table: "ServerSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupCompletedUtc",
                table: "ServerSettings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitHubCheckEnabled",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LastNotifiedVersion",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LastUpdateCheckUtc",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LatestManifestUrl",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LatestNotesEn",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LatestNotesEs",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LatestPublishedUtc",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LatestReleaseUrl",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LatestVersion",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "NotifyNewVersion",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "PublicBaseUrl",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "SetupCompletedUtc",
                table: "ServerSettings");
        }
    }
}
