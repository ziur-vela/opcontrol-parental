using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job3Blocking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Blocked",
                table: "VisitRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "RuleHistory",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Identity",
                table: "RuleHistory",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "RuleHistory",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleId",
                table: "RuleHistory",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "OverrideEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlockEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Identity = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ChildCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChildCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstalledApps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identity = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Publisher = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    IsNew = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstalledApps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemBonuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Identity = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Minutes = table.Column<int>(type: "integer", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemBonuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleRuntimeStates",
                columns: table => new
                {
                    ChildId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveScheduleName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleRuntimeStates", x => x.ChildId);
                });

            migrationBuilder.CreateTable(
                name: "CategoryDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AddedVersion = table.Column<int>(type: "integer", nullable: false),
                    RemovedVersion = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryDomains_Categories_CategoryKey",
                        column: x => x.CategoryKey,
                        principalTable: "Categories",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitRecords_ChildId_Blocked_VisitedUtc",
                table: "VisitRecords",
                columns: new[] { "ChildId", "Blocked", "VisitedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleHistory_ChildId_RuleType_Identity_ChangedUtc",
                table: "RuleHistory",
                columns: new[] { "ChildId", "RuleType", "Identity", "ChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BlockEvents_FamilyId_ChildId_OccurredUtc",
                table: "BlockEvents",
                columns: new[] { "FamilyId", "ChildId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryDomains_CategoryKey_AddedVersion",
                table: "CategoryDomains",
                columns: new[] { "CategoryKey", "AddedVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryDomains_CategoryKey_Domain",
                table: "CategoryDomains",
                columns: new[] { "CategoryKey", "Domain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategoryDomains_CategoryKey_RemovedVersion",
                table: "CategoryDomains",
                columns: new[] { "CategoryKey", "RemovedVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_ChildCategories_ChildId_CategoryKey",
                table: "ChildCategories",
                columns: new[] { "ChildId", "CategoryKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstalledApps_ChildId_IsNew",
                table: "InstalledApps",
                columns: new[] { "ChildId", "IsNew" });

            migrationBuilder.CreateIndex(
                name: "IX_InstalledApps_DeviceId_Identity",
                table: "InstalledApps",
                columns: new[] { "DeviceId", "Identity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstalledApps_FamilyId_ChildId_RemovedUtc",
                table: "InstalledApps",
                columns: new[] { "FamilyId", "ChildId", "RemovedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemBonuses_ChildId_RuleType_Identity",
                table: "ItemBonuses",
                columns: new[] { "ChildId", "RuleType", "Identity" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemBonuses_FamilyId_ChildId_UntilUtc",
                table: "ItemBonuses",
                columns: new[] { "FamilyId", "ChildId", "UntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockEvents");

            migrationBuilder.DropTable(
                name: "CategoryDomains");

            migrationBuilder.DropTable(
                name: "ChildCategories");

            migrationBuilder.DropTable(
                name: "InstalledApps");

            migrationBuilder.DropTable(
                name: "ItemBonuses");

            migrationBuilder.DropTable(
                name: "ScheduleRuntimeStates");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_VisitRecords_ChildId_Blocked_VisitedUtc",
                table: "VisitRecords");

            migrationBuilder.DropIndex(
                name: "IX_RuleHistory_ChildId_RuleType_Identity_ChangedUtc",
                table: "RuleHistory");

            migrationBuilder.DropColumn(
                name: "Blocked",
                table: "VisitRecords");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "RuleHistory");

            migrationBuilder.DropColumn(
                name: "Identity",
                table: "RuleHistory");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "RuleHistory");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "RuleHistory");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "OverrideEvents");
        }
    }
}
