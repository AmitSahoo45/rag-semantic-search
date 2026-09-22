using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SemanticRagSearch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantApiKeyHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "api_key_hash",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "tenants",
                keyColumn: "tenant_id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "api_key_hash",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_api_key_hash",
                table: "tenants",
                column: "api_key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenants_api_key_hash",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "api_key_hash",
                table: "tenants");
        }
    }
}
