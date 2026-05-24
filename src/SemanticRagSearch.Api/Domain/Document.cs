using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace SemanticRagSearch.Api.Domain
{
    public class Document
    {
        [Key]
        public Guid DocumentId { get; set; } = Guid.NewGuid();

        public required Guid TenantId { get; set; }

        public required string Title {  get; set; }

        public string? SourceUrl { get; set; }

        public required string ContentHash {  get; set; }

        public DateTime IngestedAt { get; set; } = DateTime.UtcNow;

        public Tenant Tenant { get; set; } = null!;

        public List<Chunk> Chunks { get; set; } = [];
    }
}
