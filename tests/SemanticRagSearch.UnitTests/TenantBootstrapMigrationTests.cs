using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Pgvector.EntityFrameworkCore;
using SemanticRagSearch.Api.Data;
using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.UnitTests;

public class TenantBootstrapMigrationTests
{
    private static readonly Guid DevelopmentTenantId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void Migrations_InsertActiveDevelopmentTenant()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=unused;Username=unused;Password=unused",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;
        using var dbContext = new AppDbContext(options);
        IMigrationsAssembly migrations = dbContext.GetService<IMigrationsAssembly>();

        InsertDataOperation insert = Assert.Single(
            migrations.Migrations.Values
                .Select(type => migrations.CreateMigration(
                    type,
                    "Npgsql.EntityFrameworkCore.PostgreSQL"))
                .SelectMany(migration => migration.UpOperations)
                .OfType<InsertDataOperation>(),
            operation => operation.Table == "tenants");

        Assert.Equal(1, insert.Values.GetLength(0));
        var row = insert.Columns
            .Select((column, index) => new { column, value = insert.Values[0, index] })
            .ToDictionary(item => item.column, item => item.value);

        Assert.Equal(
            DevelopmentTenantId,
            row["tenant_id"]);
        Assert.Equal("Development Tenant", row["tenant_name"]);
        Assert.Equal(
            "Default tenant for local Phase 1-2 development.",
            row["tenant_description"]);
        Assert.Equal(
            "development@semanticragsearch.invalid",
            row["email"]);
        Assert.Null(row["api_key"]);
        Assert.Equal(true, row["is_active"]);
        Assert.Equal(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            row["created_date"]);
        Assert.Equal(row["created_date"], row["last_modified_date"]);
    }

    [Fact]
    public void Model_DefinesActiveDevelopmentTenantSeed()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=unused;Username=unused;Password=unused",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;
        using var dbContext = new AppDbContext(options);

        IModel designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
        var tenantType = designTimeModel.FindEntityType(typeof(Tenant));
        Assert.NotNull(tenantType);
        IDictionary<string, object?> seed = Assert.Single(
            tenantType.GetSeedData());

        Assert.Equal(DevelopmentTenantId, seed[nameof(Tenant.TenantId)]);
        Assert.Equal("Development Tenant", seed[nameof(Tenant.TenantName)]);
        Assert.Equal(
            "Default tenant for local Phase 1-2 development.",
            seed[nameof(Tenant.TenantDescription)]);
        Assert.Equal(
            "development@semanticragsearch.invalid",
            seed[nameof(Tenant.Email)]);
        Assert.False(
            seed.TryGetValue(nameof(Tenant.ApiKey), out object? apiKey) &&
            apiKey is not null);
        Assert.Equal(true, seed[nameof(Tenant.IsActive)]);
        Assert.Equal(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            seed[nameof(Tenant.CreatedDate)]);
        Assert.Equal(
            seed[nameof(Tenant.CreatedDate)],
            seed[nameof(Tenant.LastModifiedDate)]);
    }

    [Fact]
    public void Migrations_RollbackDeletesOnlyDevelopmentTenant()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=unused;Username=unused;Password=unused",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;
        using var dbContext = new AppDbContext(options);
        IMigrationsAssembly migrations = dbContext.GetService<IMigrationsAssembly>();

        DeleteDataOperation delete = Assert.Single(
            migrations.Migrations.Values
                .Select(type => migrations.CreateMigration(
                    type,
                    "Npgsql.EntityFrameworkCore.PostgreSQL"))
                .SelectMany(migration => migration.DownOperations)
                .OfType<DeleteDataOperation>(),
            operation => operation.Table == "tenants");

        Assert.Equal(["tenant_id"], delete.KeyColumns);
        Assert.Equal(1, delete.KeyValues.GetLength(0));
        Assert.Equal(1, delete.KeyValues.GetLength(1));
        Assert.Equal(DevelopmentTenantId, delete.KeyValues[0, 0]);
    }
}
