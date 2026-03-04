using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Streams;
using Memorizer.Models;
using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    private readonly IServiceProvider _serviceProvider;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IRequiredActor<EmbeddingRegenerationActorKey> _embeddingRegenerationActor;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    // PostgreSQL advisory lock ID for dimension migrations
    private const int MigrationLockId = 19283746;

    // Progress manager - handles subscriber management and job state
    private ProgressJobManager? _jobManager;

    // Current scope for the running job
    private IServiceScope? _currentScope;

    // Current migration state
    private Guid? _currentMigrationId;
    private DimensionMigrationRecord? _currentMigration;
    private int _newDimensions;
    private int _oldDimensions;
    private string? _oldModel;
    private string? _newModel;
    private DateTime _startTime;
    private string _requestedBy = "system";

    public DimensionMigrationActor(
        IServiceProvider serviceProvider,
        NpgsqlDataSource dataSource,
        IRequiredActor<EmbeddingRegenerationActorKey> embeddingRegenerationActor)
    {
        _serviceProvider = serviceProvider;
        _dataSource = dataSource;
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

        // Handle completion messages from background progress consumer
        // These messages are sent to Self from Task.Run to ensure Become() is called on the actor's mailbox thread
        ReceiveAsync<RegenerationProgressCompleted>(async msg =>
        {
            await HandleRegenerationCompleted(msg.Progress);
        });

        Receive<RegenerationProgressFailed>(msg =>
        {
            _logger.Error("Regeneration progress stream failed: {0}", msg.ErrorMessage);
            _jobManager?.Fail(msg.ErrorMessage);

            // Clean up and return to idle - must be called from mailbox thread
            _ = ReleaseDistributedLock();
            _currentMigrationId = null;
            _currentMigration = null;
            _jobManager = null;
            Become(Idle);
        });
    }

    private async Task HandleStartMigration(StartDimensionMigration msg)
    {
        var sender = Sender;
        _requestedBy = msg.RequestedBy;

        _logger.Info("Starting dimension migration requested by {0}", msg.RequestedBy);

        try
        {
            // Create a scope for the duration of this migration
            _currentScope = _serviceProvider.CreateScope();
            var dimensionService = _currentScope.ServiceProvider.GetRequiredService<IEmbeddingDimensionService>();
            var embeddingSettings = _currentScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>().Value;
            _newModel = embeddingSettings.Model;

            // Validate that there's actually a mismatch to migrate
            var validation = await dimensionService.ValidateAsync();

            if (!validation.RequiresMigration)
            {
                _logger.Info("No dimension migration required - all sources match");
                _currentScope?.Dispose();
                _currentScope = null;
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
                _currentScope?.Dispose();
                _currentScope = null;
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
                _currentScope?.Dispose();
                _currentScope = null;
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
                _newModel!, _newDimensions,
                msg.RequestedBy);

            _startTime = DateTime.UtcNow;

            // Perform schema change BEFORE replying - if this fails, we want to tell the sender
            await PerformSchemaChange(_newDimensions);

            // Schema change succeeded - now switch to Running state and reply
            Become(Running);

            // Record schema change step as complete
            _jobManager.RecordSuccess();

            // Update migration status
            await UpdateMigrationStatus(_currentMigrationId.Value, "regenerating");

            // Send status AFTER schema change succeeds - this ensures errors are properly reported
            sender.Tell(new DimensionMigrationStatus(
                IsRunning: true,
                Status: "Running - regenerating embeddings",
                OldDimensions: _oldDimensions,
                NewDimensions: _newDimensions,
                OldModel: _oldModel,
                NewModel: _newModel,
                TotalMemories: totalCount,
                MigrationId: _currentMigrationId,
                StartTime: _startTime,
                RequestedBy: msg.RequestedBy
            ));

            // Tell EmbeddingRegenerationActor to regenerate all embeddings
            _logger.Info("Schema change complete, triggering embedding regeneration");
            _embeddingRegenerationActor.ActorRef.Tell(
                new RegenerateAllEmbeddings(PageSize: 100, RequestedBy: $"dimension-migration-{_currentMigrationId}"),
                Self);

            // Subscribe to the EmbeddingRegenerationActor's progress stream and forward to our subscribers
            await SubscribeToRegenerationProgress();
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

            // Create a scope for the duration of this migration
            _currentScope = _serviceProvider.CreateScope();
            var embeddingSettings = _currentScope.ServiceProvider.GetRequiredService<IOptionsSnapshot<EmbeddingSettings>>().Value;
            _newModel = embeddingSettings.Model;

            _currentMigrationId = msg.MigrationId;
            _currentMigration = migration;
            _newDimensions = migration.NewDimensions;
            _startTime = migration.StartedAt;

            Become(Running);

            // Get total count for progress tracking
            var totalCount = await GetTotalMemoryCount();

            // Create job manager for progress tracking - use total memories + 1 for schema change step
            _jobManager = new ProgressJobManager(_logger, _materializer);
            _jobManager.StartJob(totalCount + 1, msg.RequestedBy);

            // If schema was already changed, just trigger regeneration
            if (migration.Status is "schema_changed" or "regenerating")
            {
                _logger.Info("Schema already changed, resuming embedding regeneration");
                await UpdateMigrationStatus(_currentMigrationId.Value, "regenerating");

                // Schema change already done, record it as complete
                _jobManager.RecordSuccess();

                _embeddingRegenerationActor.ActorRef.Tell(
                    new RegenerateAllEmbeddings(PageSize: 100, RequestedBy: $"dimension-migration-resume-{_currentMigrationId}"),
                    Self);

                // Subscribe to the EmbeddingRegenerationActor's progress stream
                await SubscribeToRegenerationProgress();
            }
            else
            {
                // Need to do the full migration
                await PerformSchemaChange(_newDimensions);
                await UpdateMigrationStatus(_currentMigrationId.Value, "regenerating");

                // Schema change complete, record it as successful
                _jobManager.RecordSuccess();

                _embeddingRegenerationActor.ActorRef.Tell(
                    new RegenerateAllEmbeddings(PageSize: 100, RequestedBy: $"dimension-migration-{_currentMigrationId}"),
                    Self);

                // Subscribe to the EmbeddingRegenerationActor's progress stream
                await SubscribeToRegenerationProgress();
            }

            sender.Tell(CreateCurrentStatus("Resumed"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to resume migration: {0}", ex.Message);

            await ReleaseDistributedLock();
            _currentScope?.Dispose();
            _currentScope = null;

            sender.Tell(new DimensionMigrationStatus(
                IsRunning: false,
                Status: "Failed",
                ErrorMessage: ex.Message
            ));

            Become(Idle);
        }
    }

    /// <summary>
    /// Subscribe to the EmbeddingRegenerationActor's progress stream and forward events to our subscribers.
    /// This properly piggybacks on the regeneration actor's progress using its standard subscription mechanism.
    /// </summary>
    private async Task SubscribeToRegenerationProgress()
    {
        var subscriberId = $"dimension-migration-{_currentMigrationId}";
        var self = Self; // Capture Self for use in background task

        try
        {
            // Subscribe to the EmbeddingRegenerationActor's progress stream
            var subscription = await _embeddingRegenerationActor.ActorRef.Ask<ProgressSubscription>(
                new SubscribeToProgress(subscriberId),
                TimeSpan.FromSeconds(10));

            // Consume the channel and forward progress to our job manager
            // IMPORTANT: This runs on a background thread, so we MUST NOT call Become() or any actor
            // context methods directly. Instead, we send messages to Self to handle state transitions
            // on the actor's mailbox thread.
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var progress in subscription.Reader.ReadAllAsync())
                    {
                        // Forward progress to our job manager for SSE subscribers
                        // Add 1 to account for the schema change step we already completed
                        if (_jobManager != null)
                        {
                            var totalWithSchema = progress.TotalItems + 1;
                            var processedWithSchema = progress.TotalProcessed + 1; // +1 for schema step

                            _jobManager.ReportProgress(
                                processedCount: processedWithSchema,
                                totalItems: totalWithSchema,
                                successCount: progress.TotalSuccessful + 1, // +1 for successful schema change
                                failureCount: progress.TotalFailed,
                                statusMessage: $"Regenerating embeddings: {progress.TotalProcessed}/{progress.TotalItems} " +
                                              $"({progress.TotalSuccessful} successful, {progress.TotalFailed} failed)");
                        }

                        // Check if the regeneration completed
                        if (progress.IsCompleted)
                        {
                            // Send message to self instead of calling async method directly
                            // This ensures Become(Idle) is called on the actor's mailbox thread
                            self.Tell(new RegenerationProgressCompleted(progress));
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error consuming regeneration progress stream: {0}", ex.Message);
                    // Send failure message to self for proper cleanup on mailbox thread
                    self.Tell(new RegenerationProgressFailed(ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to subscribe to regeneration progress: {0}", ex.Message);
        }
    }

    private async Task HandleRegenerationCompleted(ProgressEvent progress)
    {
        _logger.Info("Embedding regeneration completed: {0}/{1} successful, {2} failed",
            progress.TotalSuccessful, progress.TotalProcessed, progress.TotalFailed);

        try
        {
            // Restore indexes and NOT NULL constraints now that all embeddings are regenerated
            await RestoreConstraintsAndIndexes(_newDimensions);

            // Update embedding_config with new model/dimensions using scoped service
            if (_currentScope != null)
            {
                var dimensionService = _currentScope.ServiceProvider.GetRequiredService<IEmbeddingDimensionService>();
                await dimensionService.UpdateActiveConfigAsync(_newModel!, _newDimensions);

                var validation = await dimensionService.ValidateAsync();
                var mismatchState = _currentScope.ServiceProvider.GetRequiredService<IDimensionMismatchState>();
                mismatchState.Update(validation);
            }

            // Complete the migration record
            await CompleteMigration(
                _currentMigrationId!.Value,
                progress.TotalProcessed,
                progress.TotalSuccessful,
                progress.TotalFailed,
                progress.FailedIds ?? []);

            // Publish completion event
            var migration = await GetMigrationRecord(_currentMigrationId.Value);
            Context.System.EventStream.Publish(new DimensionMigrationCompleted(
                MigrationId: _currentMigrationId.Value,
                OldModel: migration?.OldModel ?? "unknown",
                OldDimensions: migration?.OldDimensions ?? 0,
                NewModel: _newModel ?? "unknown",
                NewDimensions: _newDimensions,
                TotalProcessed: progress.TotalProcessed,
                Successful: progress.TotalSuccessful,
                Failed: progress.TotalFailed,
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
            await ReleaseDistributedLock();
            _currentMigrationId = null;
            _currentMigration = null;
            _jobManager = null;
            _currentScope?.Dispose();
            _currentScope = null;
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
            NewModel: _newModel,
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

    // Intentionally counts ALL memories (including System and Archived) because dimension migration
    // must regenerate embeddings for every memory type to maintain database consistency.
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

        // Intentionally counts ALL memories (including System and Archived) for migration tracking
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
        List<MemoryId> failedIds)
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
        cmd.Parameters.AddWithValue("failedIds", failedIds.Select(id => id.Value).ToArray());
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

            // Drop NOT NULL constraints so we can set embeddings to NULL during migration
            _logger.Debug("Dropping NOT NULL constraints on embedding columns");

            await using (var cmd = new NpgsqlCommand(
                "ALTER TABLE memories ALTER COLUMN embedding DROP NOT NULL", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(
                "ALTER TABLE memories ALTER COLUMN embedding_metadata DROP NOT NULL", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Clear existing embeddings - pgvector can't ALTER TYPE with existing data
            _logger.Debug("Clearing existing embeddings before schema change");

            await using (var cmd = new NpgsqlCommand(
                "UPDATE memories SET embedding = NULL, embedding_metadata = NULL",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Now ALTER columns - they're all NULL so this will succeed
            _logger.Debug("Altering embedding column to VECTOR({0})", newDimensions);

            await using (var cmd = new NpgsqlCommand(
                $"ALTER TABLE memories ALTER COLUMN embedding TYPE VECTOR({newDimensions})",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            _logger.Debug("Altering embedding_metadata column to VECTOR({0})", newDimensions);

            await using (var cmd = new NpgsqlCommand(
                $"ALTER TABLE memories ALTER COLUMN embedding_metadata TYPE VECTOR({newDimensions})",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // NOTE: We don't recreate indexes or NOT NULL constraints here.
            // They will be restored after embedding regeneration completes successfully.
            // This avoids index maintenance overhead during bulk updates.

            await transaction.CommitAsync();

            _logger.Info("Schema change completed successfully (indexes and constraints will be restored after regeneration)");

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

    /// <summary>
    /// Restore indexes and NOT NULL constraints after embedding regeneration completes.
    /// Called from HandleRegenerationCompleted.
    /// </summary>
    private async Task RestoreConstraintsAndIndexes(int dimensions)
    {
        _logger.Info("Restoring indexes and NOT NULL constraints after regeneration");

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        try
        {
            // Restore NOT NULL constraints
            _logger.Debug("Restoring NOT NULL constraints on embedding columns");

            await using (var cmd = new NpgsqlCommand(
                "ALTER TABLE memories ALTER COLUMN embedding SET NOT NULL", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(
                "ALTER TABLE memories ALTER COLUMN embedding_metadata SET NOT NULL", conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Recreate indexes
            _logger.Debug("Recreating embedding indexes");

            await using (var cmd = new NpgsqlCommand(
                $"CREATE INDEX IF NOT EXISTS idx_memories_embedding_cosine ON memories USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100)",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new NpgsqlCommand(
                $"CREATE INDEX IF NOT EXISTS idx_memories_embedding_metadata_cosine ON memories USING ivfflat (embedding_metadata vector_cosine_ops) WITH (lists = 100)",
                conn, transaction))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();

            _logger.Info("Indexes and constraints restored successfully");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.Error(ex, "Failed to restore indexes and constraints: {0}", ex.Message);
            throw;
        }
    }

    #endregion

}
