using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SemanticRagSearch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkEmbeddingHnswIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_extension
                        WHERE extname = 'vector'
                          AND regexp_match(extversion, '^([0-9]+)\.([0-9]+)\.([0-9]+)')::int[] >= ARRAY[0, 8, 0]
                    ) THEN
                        RAISE EXCEPTION 'pgvector 0.8.0 or later is required for tenant-filtered HNSW searches. Upgrade the vector extension before applying this migration.'
                            USING ERRCODE = '0A000';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_chunks_embedding",
                table: "chunks",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chunks_embedding",
                table: "chunks");
        }
    }
}
