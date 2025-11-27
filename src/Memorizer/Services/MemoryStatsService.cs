using Npgsql;
using Registrator.Net;

namespace Memorizer.Services;

public interface IMemoryStatsService
{
    /// <summary>
    /// Gets statistics about the memory storage
    /// </summary>
    Task<MemoryStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

public class MemoryStats
{
    /// <summary>
    /// Total count of memories in the database
    /// </summary>
    public int TotalMemories { get; set; }

    /// <summary>
    /// Average size of memory content in bytes
    /// </summary>
    public long AverageMemorySizeBytes { get; set; }

    /// <summary>
    /// Current embedding dimensions (from database config or schema)
    /// </summary>
    public int EmbeddingDimensions { get; set; }

    /// <summary>
    /// Current embedding model name
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// Total count of version snapshots across all memories
    /// </summary>
    public int TotalVersions { get; set; }

    /// <summary>
    /// Total count of audit events across all memories
    /// </summary>
    public int TotalAuditEvents { get; set; }

    /// <summary>
    /// Estimated storage used by memories table (bytes)
    /// </summary>
    public long MemoriesStorageBytes { get; set; }

    /// <summary>
    /// Estimated storage used by versions and events tables (bytes)
    /// </summary>
    public long VersionStorageBytes { get; set; }

    /// <summary>
    /// Total estimated storage (memories + versions + events)
    /// </summary>
    public long TotalStorageBytes { get; set; }
}

[AutoRegisterInterfaces(ServiceLifetime.Singleton)]
public class MemoryStatsService : IMemoryStatsService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingDimensionService _dimensionService;

    public MemoryStatsService(NpgsqlDataSource dataSource, IEmbeddingDimensionService dimensionService)
    {
        _dataSource = dataSource;
        _dimensionService = dimensionService;
    }

    public async Task<MemoryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        int totalMemories = 0;
        long avgSizeBytes = 0;
        int totalVersions = 0;
        int totalEvents = 0;
        long memoriesStorageBytes = 0;
        long versionStorageBytes = 0;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get all stats in a single query for efficiency
        const string sql = @"
            SELECT
                (SELECT COUNT(*) FROM memories) as total_memories,
                (SELECT COALESCE(AVG(LENGTH(text)), 0) FROM memories) as avg_size,
                (SELECT COUNT(*) FROM memory_versions) as total_versions,
                (SELECT COUNT(*) FROM memory_events) as total_events,
                (SELECT pg_total_relation_size('memories')) as memories_storage,
                (SELECT pg_total_relation_size('memory_versions') + pg_total_relation_size('memory_events')) as version_storage";

        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totalMemories = reader.GetInt32(0);
                avgSizeBytes = Convert.ToInt64(reader.GetDouble(1));
                totalVersions = reader.GetInt32(2);
                totalEvents = reader.GetInt32(3);
                memoriesStorageBytes = reader.GetInt64(4);
                versionStorageBytes = reader.GetInt64(5);
            }
        }

        // Get embedding dimensions from dimension service (queries DB config/schema)
        var embeddingConfig = await _dimensionService.GetActiveConfigAsync(cancellationToken);
        var effectiveDimensions = await _dimensionService.GetEffectiveDimensionsAsync(cancellationToken);

        return new MemoryStats
        {
            TotalMemories = totalMemories,
            AverageMemorySizeBytes = avgSizeBytes,
            EmbeddingDimensions = effectiveDimensions,
            EmbeddingModel = embeddingConfig?.ModelName,
            TotalVersions = totalVersions,
            TotalAuditEvents = totalEvents,
            MemoriesStorageBytes = memoriesStorageBytes,
            VersionStorageBytes = versionStorageBytes,
            TotalStorageBytes = memoriesStorageBytes + versionStorageBytes
        };
    }
} 