using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace SemanticRagSearch.Api.Domain
{
    [Index(nameof(Email), IsUnique = true)] // When a user registers from UI, that email must be unique and not already exisitng in DB. 
    public class Tenant
    {
        [Key]
        public Guid TenantId { get; set; } = Guid.NewGuid();

        [Required]
        public required string TenantName { get; set; }

        [Required]
        public required string TenantDescription { get; set; }

        [Required]
        public string Email { get; set; } = string.Empty;

        public string? ApiKey { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public DateTime LastModifiedDate { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;

        public List<Document> Documents { get; set; } = [];

        public List<UsageRecord> UsageRecords { get; set; } = [];
    }
}
