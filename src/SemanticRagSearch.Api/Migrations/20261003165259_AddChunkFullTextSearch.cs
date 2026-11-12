using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace SemanticRagSearch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "chunks",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('english'::regconfig, text_content)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_chunks_search_vector",
                table: "chunks",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chunks_search_vector",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "chunks");
        }
    }
}
