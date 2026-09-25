using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewAppointmentNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reviews_business_id_created_at",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_customer_id",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_staff_user_id_created_at",
                table: "reviews");

            migrationBuilder.AddColumn<Guid>(
                name: "appointment_id",
                table: "reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "reviews",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "hidden");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "reviews",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_appointment_id",
                table: "reviews",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_business_id_status_created_at",
                table: "reviews",
                columns: new[] { "business_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_business_id_updated_at",
                table: "reviews",
                columns: new[] { "business_id", "updated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_customer_id_appointment_id",
                table: "reviews",
                columns: new[] { "customer_id", "appointment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviews_customer_id_created_at",
                table: "reviews",
                columns: new[] { "customer_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_staff_user_id",
                table: "reviews",
                column: "staff_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "chk_review_status",
                table: "reviews",
                sql: "\"status\" IN ('published', 'hidden', 'removed')");

            migrationBuilder.AddForeignKey(
                name: "FK_reviews_appointments_appointment_id",
                table: "reviews",
                column: "appointment_id",
                principalTable: "appointments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reviews_appointments_appointment_id",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_appointment_id",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_business_id_status_created_at",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_business_id_updated_at",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_customer_id_appointment_id",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_customer_id_created_at",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_reviews_staff_user_id",
                table: "reviews");

            migrationBuilder.DropCheckConstraint(
                name: "chk_review_status",
                table: "reviews");

            migrationBuilder.DropColumn(
                name: "appointment_id",
                table: "reviews");

            migrationBuilder.DropColumn(
                name: "status",
                table: "reviews");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "reviews");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_business_id_created_at",
                table: "reviews",
                columns: new[] { "business_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_customer_id",
                table: "reviews",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_staff_user_id_created_at",
                table: "reviews",
                columns: new[] { "staff_user_id", "created_at" });
        }
    }
}
