using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpControl.Parental.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Job11MessagingPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows that held the old "All" value (127) keep full access,
            // including the new Messaging flag (256).
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" SET "Permissions" = "Permissions" | 256 WHERE ("Permissions" & 127) = 127;
                UPDATE "AspNetUsers" SET "Permissions" = 511 WHERE "RoleInFamily" = 'Owner';
                UPDATE "Invitations" SET "Permissions" = "Permissions" | 256 WHERE ("Permissions" & 127) = 127;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" SET "Permissions" = "Permissions" & ~256 WHERE ("Permissions" & 256) = 256;
                UPDATE "Invitations" SET "Permissions" = "Permissions" & ~256 WHERE ("Permissions" & 256) = 256;
                """);
        }
    }
}
