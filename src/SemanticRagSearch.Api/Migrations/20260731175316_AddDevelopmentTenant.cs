using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SemanticRagSearch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "tenants",
                columns: new[] { "tenant_id", "api_key", "created_date", "email", "is_active", "last_modified_date", "tenant_description", "tenant_name" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "development@semanticragsearch.invalid", true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Default tenant for local Phase 1-2 development.", "Development Tenant" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "tenants",
                keyColumn: "tenant_id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"));
        }
    }
}
