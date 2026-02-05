using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetakesAllocatorCore.Migrations
{
    /// <inheritdoc />
    public partial class FlatWeaponPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add new columns for T Team
            migrationBuilder.AddColumn<int>(
                name: "T_PistolRound",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "T_Secondary",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "T_HalfBuyPrimary",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "T_FullBuyPrimary",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "T_Preferred",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            // Add new columns for CT Team
            migrationBuilder.AddColumn<int>(
                name: "CT_PistolRound",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CT_Secondary",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CT_HalfBuyPrimary",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CT_FullBuyPrimary",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CT_Preferred",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            // Drop old JSON column
            migrationBuilder.DropColumn(
                name: "WeaponPreferences",
                table: "UserSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-add JSON column
            migrationBuilder.AddColumn<string>(
                name: "WeaponPreferences",
                table: "UserSettings",
                type: "TEXT",
                maxLength: 10000,
                nullable: true);

            // Drop flat columns
            migrationBuilder.DropColumn(name: "T_PistolRound", table: "UserSettings");
            migrationBuilder.DropColumn(name: "T_Secondary", table: "UserSettings");
            migrationBuilder.DropColumn(name: "T_HalfBuyPrimary", table: "UserSettings");
            migrationBuilder.DropColumn(name: "T_FullBuyPrimary", table: "UserSettings");
            migrationBuilder.DropColumn(name: "T_Preferred", table: "UserSettings");
            migrationBuilder.DropColumn(name: "CT_PistolRound", table: "UserSettings");
            migrationBuilder.DropColumn(name: "CT_Secondary", table: "UserSettings");
            migrationBuilder.DropColumn(name: "CT_HalfBuyPrimary", table: "UserSettings");
            migrationBuilder.DropColumn(name: "CT_FullBuyPrimary", table: "UserSettings");
            migrationBuilder.DropColumn(name: "CT_Preferred", table: "UserSettings");
        }
    }
}
