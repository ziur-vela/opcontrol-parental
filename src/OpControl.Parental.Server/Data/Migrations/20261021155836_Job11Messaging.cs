using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job11Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MutedChildMessage",
                table: "AlertPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChildMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SenderParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Preset = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SpeakAloud = table.Column<bool>(type: "boolean", nullable: false),
                    RequireAck = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredByDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenByDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentReadUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChildMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChildMessages_ChildId_Direction_CreatedUtc",
                table: "ChildMessages",
                columns: new[] { "ChildId", "Direction", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChildMessages_ChildId_ParentReadUtc",
                table: "ChildMessages",
                columns: new[] { "ChildId", "ParentReadUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChildMessages_DeviceId_Direction_DeliveredUtc",
                table: "ChildMessages",
                columns: new[] { "DeviceId", "Direction", "DeliveredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChildMessages_FamilyId_ChildId_CreatedUtc",
                table: "ChildMessages",
                columns: new[] { "FamilyId", "ChildId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChildMessages");

            migrationBuilder.DropColumn(
                name: "MutedChildMessage",
                table: "AlertPreferences");
        }
    }
}
