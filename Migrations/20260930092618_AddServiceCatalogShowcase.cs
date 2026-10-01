using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceCatalogShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /* defaultValue TRUE, deliberately overriding the CLR default EF
               scaffolded (false).

               Every row that exists today was visible on the storefront the
               moment before this column did. Backfilling false would silently
               empty every existing business's public catalogue on deploy —
               the kind of data change that is invisible in review and only
               shows up as a support ticket. TRUE preserves the pre-migration
               behaviour exactly, so the column is purely additive: from here
               on, only an owner who explicitly toggles it changes anything. */
            migrationBuilder.AddColumn<bool>(
                name: "Showcase",
                table: "services",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Showcase",
                table: "services");
        }
    }
}
