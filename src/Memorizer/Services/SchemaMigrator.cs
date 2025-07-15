using Npgsql;

namespace Memorizer.Services;

public static class SchemaMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger("SchemaMigrator");
        logger.LogInformation("Starting database schema migration...");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // Ensure schema_version table exists
        var ensureVersionTable = """

                                             CREATE TABLE IF NOT EXISTS schema_version (
                                                 version INT PRIMARY KEY,
                                                 name TEXT NOT NULL,
                                                 applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
                                             );
                                         
                                 """;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = ensureVersionTable;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Get applied migrations
        var appliedMigrations = new HashSet<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_version";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                appliedMigrations.Add(reader.GetInt32(0));
            }
        }

        // Find migration scripts
        var migrationsDir = Path.Combine(AppContext.BaseDirectory, "migrations");
        if (!Directory.Exists(migrationsDir))
        {
            logger.LogWarning($"Migrations directory not found: {migrationsDir}");
            return;
        }
        var migrationFiles = Directory.GetFiles(migrationsDir, "*.sql")
            .Select(f => new { Path = f, Name = Path.GetFileName(f) })
            .Select(f => new { f.Path, f.Name, Version = ParseVersion(f.Name) })
            .Where(f => f.Version != null)
            .OrderBy(f => f.Version)
            .ToList();

        foreach (var migration in migrationFiles)
        {
            // migration.Version is guaranteed not null due to previous filtering
            int version = migration.Version!.Value;
            if (appliedMigrations.Contains(version))
            {
                logger.LogInformation($"Migration {migration.Name} already applied.");
                continue;
            }
            logger.LogInformation($"Applying migration: {migration.Name}");
            var sql = await File.ReadAllTextAsync(migration.Path, cancellationToken);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO schema_version (version, name) VALUES (@version, @name)";
                cmd.Parameters.AddWithValue("@version", version);
                cmd.Parameters.AddWithValue("@name", migration.Name);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            logger.LogInformation($"Migration {migration.Name} applied successfully.");
        }
        logger.LogInformation("Database schema migration completed successfully");
    }

    private static int? ParseVersion(string fileName)
    {
        // Expecting format: 001_description.sql
        var parts = fileName.Split('_', 2);
        if (parts.Length < 2) return null;
        if (int.TryParse(parts[0], out var version)) return version;
        return null;
    }
} 