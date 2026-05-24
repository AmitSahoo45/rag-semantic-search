using System.ComponentModel.DataAnnotations;
using SemanticRagSearch.Api.Domain.Enums;

namespace SemanticRagSearch.Api.Domain
{
    public class UsageRecord
    {
        [Key]
        public Guid UsageRecordId { get; set; } = Guid.NewGuid();   

        public Guid TenantId { get; set; }

        public required Operations Operation { get; set; } = Operations.None;

        public int InputTokens { get; set; }

        public int OutputTokens { get; set; }

        public long LatencyMs { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public Tenant Tenant { get; set; } = null!;
    }
}
