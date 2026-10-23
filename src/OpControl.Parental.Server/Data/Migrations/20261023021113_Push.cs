using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Push : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VapidPrivateKeyProtected",
                table: "ServerSettings",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VapidPublicKey",
                table: "ServerSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushPreferences",
                columns: table => new
                {
                    ParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Messages = table.Column<bool>(type: "boolean", nullable: false),
                    Tampering = table.Column<bool>(type: "boolean", nullable: false),
                    Limits = table.Column<bool>(type: "boolean", nullable: false),
                    PinOverride = table.Column<bool>(type: "boolean", nullable: false),
                    Offline = table.Column<bool>(type: "boolean", nullable: false),
                    RepeatedBlock = table.Column<bool>(type: "boolean", nullable: false),
                    NewApp = table.Column<bool>(type: "boolean", nullable: false),
                    Other = table.Column<bool>(type: "boolean", nullable: false),
                    RespectQuietHours = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushPreferences", x => x.ParentId);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSuccessUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailureCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushPreferences_FamilyId",
                table: "PushPreferences",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_FamilyId_ParentId",
                table: "PushSubscriptions",
                columns: new[] { "FamilyId", "ParentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushPreferences");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "VapidPrivateKeyProtected",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "VapidPublicKey",
                table: "ServerSettings");
        }
    }
}
