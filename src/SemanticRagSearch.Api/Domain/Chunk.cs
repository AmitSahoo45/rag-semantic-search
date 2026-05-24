using System.ComponentModel.DataAnnotations;

namespace SemanticRagSearch.Api.Domain;

public class Chunk
{
    [Key]
    public Guid ChunkId { get; set; } = Guid.NewGuid();

    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    public int ChunkIndex { get; set; }

    public required string TextContent { get; set; }

    private int _tokenCount; 
    public int TokenCount
    {
        get => _tokenCount;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(TokenCount), "Token Count cannot be negative.");
            _tokenCount = value;
        }
    }

    public float[] Embedding { get; set; } = [];

    public ChunkMetaData? MetaData { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Document Document { get; set; } = null!;

    public Tenant Tenant { get; set; } = null!;
}