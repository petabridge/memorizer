using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Streams;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Npgsql;

namespace Memorizer.Actors;

/// <summary>
/// Actor responsible for orchestrating embedding dimension migrations.
///
/// Migration flow:
/// 1. Detect dimension mismatch (model outputs different dimensions than schema)
/// 2. Acquire PostgreSQL advisory lock to prevent concurrent migrations
/// 3. Create migration record in database
/// 4. ALTER TABLE to change VECTOR column dimensions (drops indexes first)
/// 5. Recreate indexes
/// 6. Tell EmbeddingRegenerationActor to regenerate all embeddings
/// 7. Monitor completion and update embedding_config
/// 8. Release advisory lock
///
/// Uses Become/Unbecome to switch between Idle and Running states.
/// Progress is managed via ProgressJobManager which supports multiple SSE subscribers.
/// </summary>
public sealed class DimensionMigrationActor : ReceiveActor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingDimensionService _dimensionService;
    private readonly EmbeddingSettings _embeddingSettings;
    private readonly IRequiredActor<EmbeddingRegenerationActorKey> _embeddingRegenerationActor;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    // PostgreSQL advisory lock ID for dimension migrations
    private const int MigrationLockId = 19283746;

    // Progress manager - handles subscriber management and job state
    private ProgressJobManager? _jobManager;

    // Current migration state
    private Guid? _currentMigrationId;
    private DimensionMigrationRecord? _currentMigration;
    private int _newDimensions;
    private int _oldDimensions;
    private string? _oldModel;
    private DateTime _startTime;
    private string _requestedBy = "system";

    public DimensionMigrationActor(
        NpgsqlDataSource dataSource,
        IEmbeddingDimensionService dimensionService,
        EmbeddingSettings embeddingSettings,
        IRequiredActor<EmbeddingRegenerationActorKey> embeddingRegenerationActor)
    {
        _dataSource = dataSource;
        _dimensionService = dimensionService;
        _embeddingSettings = embeddingSettings;
        _embeddingRegenerationActor = embeddingRegenerationActor;
        _logger = Context.GetLogger();
        _materializer = Context.System.Materializer();

        // Start in Idle state
        Idle();
    }

    private void Idle()
    {
        ReceiveAsync<StartDimensionMigration>(HandleStartMigration);
        ReceiveAsync<ResumeDimensionMigration>(HandleResumeMigration);
        Receive<GetDimensionMigrationStatus>(_ => HandleGetStatusIdle());

        // Handle subscription requests - return idle status that completes immediately
        Receive<SubscribeToDimensionMigrationProgress>(msg =>
        {
            _logger.Debug("Subscription requested while idle, subscriber: {0}", msg.SubscriberId);
            var tempManager = new ProgressJobManager(_logger, _materializer);
            var reader = tempManager.CreateIdleSubscription(msg.SubscriberId);
            Sender.Tell(new DimensionMigrationProgressSubscription(msg.SubscriberId, reader));
        });

        Receive<UnsubscribeFromDimensionMigrationProgress>(msg =>
        {
            _logger.Debug("Unsubscribe requested while idle, subscriber: {0}", msg.SubscriberId);
        });

        // Handle completion events from EmbeddingRegenerationActor (shouldn't happen in Idle, but handle gracefully)
        Receive<BatchEmbeddingRegenerationCompleted>(_ =>
        {
            _logger.Debug("Received BatchEmbeddingRegenerationCompleted while idle, ignoring");
        });
    }

    private void Running()
    {
        // Reject new migration requests while running
        Receive<StartDimensionMigration>(msg =>
        {
            _logger.Warning("Dimension migration already in progress, declining new request from {0}", msg.RequestedBy);
            Sender.Tell(CreateCurrentStatus("Migration already in progress"));
        });

        Receive<ResumeDimensionMigration>(msg =>
        {
            _logger.Warning("Dimension migration already in progress, cannot resume {0}", msg.MigrationId);
            Sender.Tell(CreateCurrentStatus("Migration already in progress"));
        });

        Receive<GetDimensionMigrationStatus>(_ => HandleGetStatusRunning());

        // Handle subscription requests - add to active job
        Receive<SubscribeToDimensionMigrationProgress>(msg =>
        {
            if (_jobManager != null)
            {
                _logger.Debug("Adding subscriber to running job: {0}", msg.SubscriberId);
                var reader = _jobManager.AddSubscriber(msg.SubscriberId);
                Sender.Tell(new DimensionMigrationProgressSubscription(msg.SubscriberId, reader));
            }
        });

        Receive<UnsubscribeFromDimensionMigrationProgress>(msg =>
        {
            _logger.Debug("Removing subscriber: {0}", msg.SubscriberId);
            _jobManager?.RemoveSubscriber(msg.SubscriberId);
        });

        // Handle progress updates from EmbeddingRegenerationActor - forward to our subscribers
        Receive<EmbeddingRegenerationProgress>(HandleRegenerationProgress);

        // Handle completion from EmbeddingRegenerationActor
        ReceiveAsync<BatchEmbeddingRegenerationCompleted>(HandleRegenerationCompleted);
    }

    private async Task HandleStartMigration(StartDimensionMigration msg)
    {
        var sender = Sender;
        _requestedBy = msg.RequestedBy;

        _logger.Info("Starting dimension migration requested by {0}", msg.RequestedBy);

        try
        {
            // Validate that there's actually a mismatch to migrate
            var validation = await _dimensionService.ValidateAsync();

            if (!validation.RequiresMigration)
            {
                _logger.Info("No dimension migration required - all sources match");
                sender.Tell(new DimensionMigrationStatus(
                    IsRunning: false,
                    Status: "No migration required",
                    OldDimensions: validation.StoredDimensions,
                    NewDimensions: validation.DetectedModelDimensions,
                    OldModel: validation.StoredModel,
                    NewModel: validation.ConfiguredModel
                ));
                return;
            }

            if (!validation.DetectedModelDimensions.HasValue)
            {
                _logger.Error("Cannot start migration: embedding API unavailable, cannot detect model dimensions");
                sender.Tell(new DimensionMigrationStatus(
                    IsRunning: false,
                    Status: "Failed",
                    ErrorMessage: "Embedding API unavailable - cannot detect model dimensions"
                ));
                return;
            }

            _newDimensions = validation.DetectedModelDimensions.Value;
            _oldDimensions = validation.StoredDimensions ?? validation.DatabaseSchemaDimensions ?? 384;
            _oldModel = validation.StoredModel ?? "unknown";

            // Try to acquire distributed lock
            if (!await TryAcquireDistributedLock())
            {
                _logger.Warning("Could not acquire migration lock - another migration may be in progress");
                sender.Tell(new DimensionMigrationStatus(
                    IsRunning: false,
                    Status: "Failed",
                    ErrorMessage: "Could not acquire migration lock - another migration may be in progress"
                ));
                return;
            }

            // Get total count for progress tracking
            var totalCount = await GetTotalMemoryCount();

            // Create job manager and start tracking - use total memories + 1 for schema change step
            _jobManager = new ProgressJobManager(_logger, _materializer);
            _jobManager.StartJob(totalCount + 1, msg.RequestedBy);

            // Create migration record
            _currentMigrationId = await CreateMigrationRecord(
                _oldModel, _oldDimensions,
                _embeddingSettings.Model, _newDimensions,
                msg.RequestedBy);

            _startTime = DateTime.UtcNow;
            Become(Running);

            // Send initial status
            sender.Tell(new DimensionMigrationStatus(
                IsRunning: true,
                Status: "Running - changing schema",
                OldDimensions: _oldDimensions,
                NewDimensions: _newDimensions,
                OldModel: _oldModel,
                NewModel: _embeddingSettings.Model,
                TotalMemories: totalCount,
                MigrationId: _currentMigrationId,
                StartTime: _startTime,
                RequestedBy: msg.RequestedBy
            ));

            // Perform schema change
            await PerformSchemaChange(_newDimensions);

            // Record schema change step as complete
            _jobManager.RecordSuccess();

            // Update migration status
            await UpdateMigrationStatus(_currentMigrationId.Value, "regenerating");

            // Subscribe to progress and completion events from EmbeddingRegenerationActor
            Context.System.EventStream.Subscribe(Self, typeof(EmbeddingRegenerationProgress));
            Context.System.EventStream.Subscribe(Self, typeof(BatchEmbeddingRegenerationCompleted));

            // Tell EmbeddingRegenerationActor to regenerate all embeddings
            _logger.Info("Schema change complete, triggering embedding regeneration");
            _embeddingRegenerationActor.ActorRef.Tell(
                new RegenerateAllEmbeddings(PageSize: 100, RequestedBy: $"dimension-migration-{_currentMigrationId}"),
                Self);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start dimension migration: {0}", ex.Message);

            await ReleaseDistributedLock();

            if (_currentMigrationId.HasValue)
            {
                await FailMigration(_currentMigrationId.Value, ex.Message);
            }

            // Fail job manager to notify SSE subscribers
            _jobManager?.Fail(ex.Message);
            _jobManager = null;

            sender.Tell(new DimensionMigrationStatus(
                IsRunning: false,
                Status: "Failed",
                ErrorMessage: ex.Message,
                MigrationId: _currentMigrationId
            ));

            Become(Idle);
        }
    }

    private async Task HandleResumeMigration(ResumeDimensionMigration msg)
    {
        var sender = Sender;
        _requestedBy = msg.RequestedBy;

        _logger.Info("Attempting to resume migration {0} requested by {1}", msg.MigrationId, msg.RequestedBy);

        try
        {
            var migration = await GetMigrationRecord(msg.MigrationId);

            if (migration == null)
            {
                sender.Tell(new DimensionMigrationStatus(
                    IsRunning: false,
                    Status: "Failed",
                    ErrorMessage: $"Migration {msg.MigrationId} not found"
                ));
                return;
            }

            if (!migration.CanResume)
            {
                sender.Tell(new DimensionMigrationStatus(
                    IsRunning: false,
                    Status: "Failed",
                    ErrorMessage: $"Migration {msg.MigrationId} cannot be resumed (status: {migration.Status})"
                ));
                return;
            }

            // Try to acquire lock
            if (!await TryAcquireDistributedLock())
            {
                sender.Tell(new DimensionMigrationStatus(
                    IsRunning: false,
                    Status: "Failed",
                    ErrorMessage: "Could not acquire migration lock"
                ));
                return;
            }

            _currentMigrationId = msg.MigrationId;
            _currentMigration = migration;
            _newDimensions = migration.NewDimensions;
            _startTime = migration.StartedAt;

            Become(Running);

            // If schema was already changed, just trigger regeneration
            if (migration.Status is "schema_changed" or "regenerating")
            {
                _logger.Info("Schema already changed, resuming embedding regeneration");
                await UpdateMigrationStatus(_currentMigrationId.Value, "regenerating");

                Context.System.EventStream.Subscribe(Self, typeof(EmbeddingRegenerationProgress));
                Context.System.EventStream.Subscribe(Self, typeof(BatchEmbeddingRegenerationCompleted));

                _embeddingRegenerationActor.ActorRef.Tell(
                    new RegenerateAllEmbeddings(PageSize: 100, RequestedBy: $"dimension-migration-resume-{_currentMigrationId}"),
                    Self);
            }
            else
            {
                // Need to do the full migration
                await PerformSchemaChange(_newDimensions);
                await UpdateMigrationStatus(_currentMigrationId.Value, "regenerating");

                Context.System.EventStream.Subscribe(Self, typeof(EmbeddingRegenerationProgress));
                Context.System.EventStream.Subscribe(Self, typeof(BatchEmbeddingRegenerationCompleted));

                _embeddingRegenerationActor.ActorRef.Tell(
                    new RegenerateAllEmbeddings(PageSize: 100, RequestedBy: $"dimension-migration-{_currentMigrationId}"),
                    Self);
            }

            sender.Tell(CreateCurrentStatus("Resumed"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to resume migration: {0}", ex.Message);

            await ReleaseDistributedLock();

            sender.Tell(new DimensionMigrationStatus(
                IsRunning: false,
                Status: "Failed",
                ErrorMessage: ex.Message
            ));

            Become(Idle);
        }
    }

    private void HandleRegenerationProgress(EmbeddingRegenerationProgress msg)
    {
        // Only process progress for our migration's regeneration request
        if (!msg.RequestedBy.Contains(_currentMigrationId?.ToString() ?? "no-migration"))
        {
            return;
        }

        // Forward progress to our job manager for SSE subscribers
        // Add 1 to account for the schema change step we already completed
        if (_jobManager != null)
        {
            // Update our job manager with the regeneration progress
            // The +1 accounts for the schema change step
            var totalWithSchema = msg.TotalItems + 1;
            var processedWithSchema = msg.ProcessedCount + 1; // +1 for schema step

            _jobManager.ReportProgress(
                processedCount: processedWithSchema,
                totalItems: totalWithSchema,
                successCount: msg.SuccessCount + 1, // +1 for successful schema change
                failureCount: msg.FailureCount,
                statusMessage: $"Regenerating embeddings: {msg.ProcessedCount}/{msg.TotalItems} " +
                              $"({msg.SuccessCount} successful, {msg.FailureCount} failed)");
        }
    }

    private async Task HandleRegenerationCompleted(BatchEmbeddingRegenerationCompleted msg)
    {
        // Check if this completion is for our migration
        if (!msg.RequestedBy.Contains(_currentMigrationId?.ToString() ?? "no-migration"))
        {
            _logger.Debug("Received BatchEmbeddingRegenerationCompleted for different request, ignoring");
            return;
        }

        _logger.Info("Embedding regeneration completed: {0}/{1} successful, {2} failed",
            msg.TotalSuccessful, msg.TotalProcessed, msg.FailedMemoryIds.Count);

        try
        {
            // Update embedding_config with new model/dimensions
            await _dimensionService.UpdateActiveConfigAsync(_embeddingSettings.Model, _newDimensions);

            // Complete the migration record
            await CompleteMigration(
                _currentMigrationId!.Value,
                msg.TotalProcessed,
                msg.TotalSuccessful,
                msg.FailedMemoryIds.Count,
                msg.FailedMemoryIds);

            // Publish completion event
            var migration = await GetMigrationRecord(_currentMigrationId.Value);
            Context.System.EventStream.Publish(new DimensionMigrationCompleted(
                MigrationId: _currentMigrationId.Value,
                OldModel: migration?.OldModel ?? "unknown",
                OldDimensions: migration?.OldDimensions ?? 0,
                NewModel: _embeddingSettings.Model,
                NewDimensions: _newDimensions,
                TotalProcessed: msg.TotalProcessed,
                Successful: msg.TotalSuccessful,
                Failed: msg.FailedMemoryIds.Count,
                Duration: DateTime.UtcNow - _startTime,
                RequestedBy: _requestedBy
            ));

            // Complete the job manager (broadcasts completion to SSE subscribers)
            _jobManager?.Complete();

            _logger.Info("Dimension migration {0} completed successfully", _currentMigrationId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error completing migration: {0}", ex.Message);
            _jobManager?.Fail(ex.Message);
        }
        finally
        {
            Context.System.EventStream.Unsubscribe(Self, typeof(EmbeddingRegenerationProgress));
            Context.System.EventStream.Unsubscribe(Self, typeof(BatchEmbeddingRegenerationCompleted));
            await ReleaseDistributedLock();
            _currentMigrationId = null;
            _currentMigration = null;
            _jobManager = null;
            Become(Idle);
        }
    }

    private void HandleGetStatusIdle()
    {
        Sender.Tell(new DimensionMigrationStatus(
            IsRunning: false,
            Status: "Idle"
        ));
    }

    private void HandleGetStatusRunning()
    {
        Sender.Tell(CreateCurrentStatus("Running"));
    }

    private DimensionMigrationStatus CreateCurrentStatus(string statusOverride)
    {
        return new DimensionMigrationStatus(
            IsRunning: true,
            Status: statusOverride,
            OldDimensions: _currentMigration?.OldDimensions,
            NewDimensions: _newDimensions,
            OldModel: _currentMigration?.OldModel,
            NewModel: _embeddingSettings.Model,
            TotalMemories: _currentMigration?.TotalMemories ?? 0,
            Processed: _currentMigration?.MemoriesProcessed ?? 0,
            Successful: _currentMigration?.MemoriesSuccessful ?? 0,
            Failed: _currentMigration?.MemoriesFailed ?? 0,
            StartTime: _startTime,
            Duration: DateTime.UtcNow - _startTime,
            MigrationId: _currentMigrationId,
            RequestedBy: _requestedBy
        );
    }

    #region Database Operations

    private async Task<int> GetTotalMemoryCount()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM memories", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<bool> TryAcquireDistributedLock()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT pg_try_advisory_lock({MigrationLockId})", conn);

        var result = await cmd.ExecuteScalarAsync();
        return result is true;
    }

    private async Task ReleaseDistributedLock()
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"SELECT pg_advisory_unlock({MigrationLockId})", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error releasing advisory lock: {0}", ex.Message);
        }
    }

    private async Task<Guid> CreateMigrationRecord(
        string oldModel, int oldDimensions,
        string newModel, int newDimensions,
        string requestedBy)
    {
        // Get total memory count
        await using var conn = await _dataSource.OpenConnectionAsync();

        await using var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM memories", conn);
        var totalMemories = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

        const string sql = @"
            INSERT INTO embedding_dimension_migrations
                (old_model, old_dimensions, new_model, new_dimensions, total_memories, requested_by, status)
            VALUES (@oldModel, @oldDimensions, @newModel, @newDimensions, @totalMemories, @requestedBy, 'running')
            RETURNING migration_id";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("oldModel", oldModel);
        cmd.Parameters.AddWithValue("oldDimensions", oldDimensions);
        cmd.Parameters.AddWithValue("newModel", newModel);
        cmd.Parameters.AddWithValue("newDimensions", newDimensions);
        cmd.Parameters.AddWithValue("totalMemories", totalMemories);
        cmd.Parameters.AddWithValue("requestedBy", requestedBy);

        var result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
    }

    private async Task<DimensionMigrationRecord?> GetMigrationRecord(Guid migrationId)
    {
        const string sql = @"
            SELECT migration_id, started_at, completed_at, old_model, old_dimensions,
                   new_model, new_dimensions, status, error_message, total_memories,
                   memories_processed, memories_successful, memories_failed,
                   last_processed_memory_id, failed_memory_ids, requested_by
            FROM embedding_dimension_migrations
            WHERE migration_id = @migrationId";

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("migrationId", migrationId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DimensionMigrationRecord
            {
                MigrationId = reader.GetGuid(0),
                StartedAt = reader.GetDateTime(1),
                CompletedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                OldModel = reader.GetString(3),
                OldDimensions = reader.GetInt32(4),
                NewModel = reader.GetString(5),
                NewDimensions = reader.GetInt32(6),
                Status = reader.GetString(7),
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                TotalMemories = reader.GetInt32(9),
                MemoriesProcessed = reader.GetInt32(10),
                MemoriesSuccessful = reader.GetInt32(11),
                MemoriesFailed = reader.GetInt32(12),
                LastProcessedMemoryId = reader.IsDBNull(13) ? null : reader.GetGuid(13),
                FailedMemoryIds = reader.GetFieldValue<Guid[]>(14),
                RequestedBy = reader.IsDBNull(15) ? null : reader.GetString(15)
            };
        }

        return null;
    }

    private async Task UpdateMigrationStatus(Guid migrationId, string status)
    {
        const string sql = "UPDATE embedding_dimension_migrations SET status = @status WHERE migration_id = @id";

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", migrationId);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CompleteMigration(
        Guid migrationId,
        int processed, int successful, int failed,
        List<Guid> failedIds)
    {
        const string sql = @"
            UPDATE embedding_dimension_migrations
            SET status = 'completed',
                completed_at = NOW(),
                memories_processed = @processed,
                memories_successful = @successful,
                memories_failed = @failed,
                failed_memory_ids = @failedIds
            WHERE migration_id = @id";

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", migrationId);
        cmd.Parameters.AddWithValue("processed", processed);
        cmd.Parameters.AddWithValue("successful", successful);
        cmd.Parameters.AddWithValue("failed", failed);
        cmd.Parameters.AddWithValue("failedIds", failedIds.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task FailMigration(Guid migrationId, string errorMessage)
    {
        const string sql = @"
            UPDATE embedding_dimension_migrations
            SET status = 'failed', error_message = @error, completed_at = NOW()
            WHERE migration_id = @id";

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", migrationId);
        cmd.Parameters.AddWithValue("error", errorMessage);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task PerformSchemaChange(int newDimensions)
    {
        _logger.Info("Performing schema change to VECTOR({0})", newDimensions);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        try
        {
            // Drop indexes first (pgvector requires this for ALTER)
            _logger.Debug("Dropping embedding indexes");

            await using (var cmd = new NpgsqlCommand(
                "DROP INDEX IF EXISTS idx_memories_embedding_cosine", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(
                "DROP INDEX IF EXISTS idx_memories_embedding_metadata_cosine", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // ALTER columns - use USING NULL to clear existing values (we'll regenerate them)
            _logger.Debug("Altering embedding column to VECTOR({0})", newDimensions);

            await using (var cmd = new NpgsqlCommand(
                $"ALTER TABLE memories ALTER COLUMN embedding TYPE VECTOR({newDimensions}) USING NULL::VECTOR({newDimensions})",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            _logger.Debug("Altering embedding_metadata column to VECTOR({0})", newDimensions);

            await using (var cmd = new NpgsqlCommand(
                $"ALTER TABLE memories ALTER COLUMN embedding_metadata TYPE VECTOR({newDimensions}) USING NULL::VECTOR({newDimensions})",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Recreate indexes
            _logger.Debug("Recreating embedding indexes");

            await using (var cmd = new NpgsqlCommand(
                $"CREATE INDEX idx_memories_embedding_cosine ON memories USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100)",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(
                $"CREATE INDEX idx_memories_embedding_metadata_cosine ON memories USING ivfflat (embedding_metadata vector_cosine_ops) WITH (lists = 100)",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();

            _logger.Info("Schema change completed successfully");

            // Update migration status
            if (_currentMigrationId.HasValue)
            {
                await UpdateMigrationStatus(_currentMigrationId.Value, "schema_changed");
            }
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    #endregion

    public static Props Props(
        NpgsqlDataSource dataSource,
        IEmbeddingDimensionService dimensionService,
        EmbeddingSettings embeddingSettings,
        IRequiredActor<EmbeddingRegenerationActorKey> embeddingRegenerationActor)
    {
        return Akka.Actor.Props.Create(() => new DimensionMigrationActor(
            dataSource, dimensionService, embeddingSettings, embeddingRegenerationActor));
    }
}
