using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPhoneNumberIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_users_phone_number",
                table: "users",
                column: "phone_number",
                filter: "phone_number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_phone_number",
                table: "users");
        }
    }
}
