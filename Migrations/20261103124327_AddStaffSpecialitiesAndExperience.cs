using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffSpecialitiesAndExperience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "specialities",
                table: "business_memberships",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "years_of_experience",
                table: "business_memberships",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_business_memberships_years_experience",
                table: "business_memberships",
                sql: "\"years_of_experience\" IS NULL OR (\"years_of_experience\" >= 0 AND \"years_of_experience\" <= 80)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_business_memberships_years_experience",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "specialities",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "years_of_experience",
                table: "business_memberships");
        }
    }
}
