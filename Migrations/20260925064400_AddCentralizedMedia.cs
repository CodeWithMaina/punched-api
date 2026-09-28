using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralizedMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "avatar_media_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cover_media_id",
                table: "businesses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "logo_media_id",
                table: "businesses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "r2"),
                    source_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    visibility = table.Column<int>(type: "integer", nullable: false),
                    upload_grant_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    upload_attempt = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    expected_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    declared_mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    detected_mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    source_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    processing_lease_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    processing_lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processing_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    processing_recipe = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    variants_json = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    delivery_purge_status = table.Column<int>(type: "integer", nullable: false),
                    delivery_purge_attempts = table.Column<int>(type: "integer", nullable: false),
                    delivery_purge_next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivery_purge_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    delivery_purged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media", x => x.id);
                    table.CheckConstraint("ck_media_attempts", "\"upload_attempt\" >= 1 AND \"processing_attempts\" >= 0");
                    table.CheckConstraint("ck_media_dimensions", "(\"width\" IS NULL OR \"width\" > 0) AND (\"height\" IS NULL OR \"height\" > 0)");
                    table.CheckConstraint("ck_media_expected_size", "\"expected_size_bytes\" IS NULL OR \"expected_size_bytes\" > 0");
                    table.CheckConstraint("ck_media_owner_xor", "(\"business_id\" IS NULL) <> (\"owner_user_id\" IS NULL)");
                    table.CheckConstraint("ck_media_provider_r2", "\"provider\" = 'r2'");
                    table.CheckConstraint("ck_media_purge_attempts", "\"delivery_purge_attempts\" >= 0");
                    table.CheckConstraint("ck_media_purge_status", "\"delivery_purge_status\" IN (0,1,2,3)");
                    table.CheckConstraint("ck_media_source_size", "\"source_size_bytes\" IS NULL OR \"source_size_bytes\" > 0");
                    table.CheckConstraint("ck_media_status", "\"status\" IN (0,1,2,3,4,5,6)");
                    table.CheckConstraint("ck_media_visibility", "\"visibility\" IN (0,1)");
                    table.ForeignKey(
                        name: "FK_media_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_users_uploaded_by_user_id",
                        column: x => x.uploaded_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "business_media",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Gallery"),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_featured = table.Column<bool>(type: "boolean", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_media", x => new { x.business_id, x.media_id });
                    table.CheckConstraint("ck_business_media_order", "\"sort_order\" >= 0");
                    table.CheckConstraint("ck_business_media_role", "\"role\" = 'Gallery'");
                    table.ForeignKey(
                        name: "FK_business_media_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_media_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_program_media",
                columns: table => new
                {
                    loyalty_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_program_media", x => new { x.loyalty_program_id, x.media_id });
                    table.CheckConstraint("ck_loyalty_program_media_order", "\"sort_order\" >= 0");
                    table.ForeignKey(
                        name: "FK_loyalty_program_media_loyalty_programs_loyalty_program_id",
                        column: x => x.loyalty_program_id,
                        principalTable: "loyalty_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_program_media_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_media",
                columns: table => new
                {
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_media", x => new { x.review_id, x.media_id });
                    table.CheckConstraint("ck_review_media_order", "\"sort_order\" >= 0");
                    table.ForeignKey(
                        name: "FK_review_media_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_review_media_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_media",
                columns: table => new
                {
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_media", x => new { x.service_id, x.media_id });
                    table.CheckConstraint("ck_service_media_order", "\"sort_order\" >= 0");
                    table.ForeignKey(
                        name: "FK_service_media_media_media_id",
                        column: x => x.media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_media_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_avatar_media_id",
                table: "users",
                column: "avatar_media_id");

            migrationBuilder.CreateIndex(
                name: "IX_businesses_cover_media_id",
                table: "businesses",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "IX_businesses_logo_media_id",
                table: "businesses",
                column: "logo_media_id");

            migrationBuilder.CreateIndex(
                name: "IX_business_media_media_id",
                table: "business_media",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_media_order",
                table: "business_media",
                columns: new[] { "business_id", "sort_order", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_business_media_one_featured",
                table: "business_media",
                column: "business_id",
                unique: true,
                filter: "\"is_featured\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_program_media_media_id",
                table: "loyalty_program_media",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_program_media_order",
                table: "loyalty_program_media",
                columns: new[] { "loyalty_program_id", "sort_order", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_business_status_purpose_created",
                table: "media",
                columns: new[] { "business_id", "status", "purpose", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_cleanup",
                table: "media",
                columns: new[] { "status", "upload_grant_expires_at", "next_attempt_at", "processing_lease_until" });

            migrationBuilder.CreateIndex(
                name: "ix_media_delivery_purge",
                table: "media",
                columns: new[] { "delivery_purge_status", "delivery_purge_next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_owner_status_purpose_created",
                table: "media",
                columns: new[] { "owner_user_id", "status", "purpose", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_uploader_created",
                table: "media",
                columns: new[] { "uploaded_by_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_media_source_key",
                table: "media",
                column: "source_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_media_media_id",
                table: "review_media",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_media_order",
                table: "review_media",
                columns: new[] { "review_id", "sort_order", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_media_media_id",
                table: "service_media",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_media_order",
                table: "service_media",
                columns: new[] { "service_id", "sort_order", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_businesses_media_cover_media_id",
                table: "businesses",
                column: "cover_media_id",
                principalTable: "media",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_businesses_media_logo_media_id",
                table: "businesses",
                column: "logo_media_id",
                principalTable: "media",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_users_media_avatar_media_id",
                table: "users",
                column: "avatar_media_id",
                principalTable: "media",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_businesses_media_cover_media_id",
                table: "businesses");

            migrationBuilder.DropForeignKey(
                name: "FK_businesses_media_logo_media_id",
                table: "businesses");

            migrationBuilder.DropForeignKey(
                name: "FK_users_media_avatar_media_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "business_media");

            migrationBuilder.DropTable(
                name: "loyalty_program_media");

            migrationBuilder.DropTable(
                name: "review_media");

            migrationBuilder.DropTable(
                name: "service_media");

            migrationBuilder.DropTable(
                name: "media");

            migrationBuilder.DropIndex(
                name: "IX_users_avatar_media_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_businesses_cover_media_id",
                table: "businesses");

            migrationBuilder.DropIndex(
                name: "IX_businesses_logo_media_id",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "avatar_media_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "cover_media_id",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "logo_media_id",
                table: "businesses");
        }
    }
}
