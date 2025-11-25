using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Memorizer.Services;
using Pgvector;

namespace Memorizer.Actors;

/// <summary>
/// Actor responsible for generating metadata embeddings for all memories using offset/limit pagination.
/// Uses Become/Unbecome to switch between Idle and Running states.
/// Progress is managed via ProgressJobManager which supports multiple SSE subscribers.
/// </summary>
public sealed class MetadataEmbeddingActor : ReceiveActor
{
    private readonly IStorage _storage;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    // Progress manager - handles subscriber management and job state
    private ProgressJobManager? _jobManager;

    // Pagination state
    private int _currentPage;
    private int _pageSize;
    private int _outstandingOnCurrentPage;

    public MetadataEmbeddingActor(
        IStorage storage,
        IEmbeddingService embeddingService)
    {
        _storage = storage;
        _embeddingService = embeddingService;
        _logger = Context.GetLogger();
        _materializer = Context.System.Materializer();

        // Start in Idle state
        Idle();
    }

    private void Idle()
    {
        // Idle behavior - waiting for work
        ReceiveAsync<RegenerateAllMetadataEmbeddings>(HandleRegenerateAll);

        // Handle subscription requests - return idle status that completes immediately
        Receive<SubscribeToProgress>(msg =>
        {
            _logger.Debug("Subscription requested while idle, subscriber: {0}", msg.SubscriberId);
            // Create a temporary job manager just to create an idle subscription
            var tempManager = new ProgressJobManager(_logger, _materializer);
            var reader = tempManager.CreateIdleSubscription(msg.SubscriberId);
            Sender.Tell(new ProgressSubscription(msg.SubscriberId, reader));
        });

        Receive<UnsubscribeFromProgress>(msg =>
        {
            _logger.Debug("Unsubscribe requested while idle, subscriber: {0}", msg.SubscriberId);
            // No active job manager, nothing to clean up
        });

        Receive<GetMetadataEmbeddingStatus>(_ => HandleGetStatusIdle());
    }

    private void Running()
    {
        // Running behavior - actively processing batch
        ReceiveAsync<GenerateMetadataEmbeddingForMemory>(HandleGenerateMetadataEmbeddingForMemory);

        // Handle subscription requests - add to active job
        Receive<SubscribeToProgress>(msg =>
        {
            if (_jobManager != null)
            {
                _logger.Debug("Adding subscriber to running job: {0}", msg.SubscriberId);
                var reader = _jobManager.AddSubscriber(msg.SubscriberId);
                Sender.Tell(new ProgressSubscription(msg.SubscriberId, reader));
            }
        });

        Receive<UnsubscribeFromProgress>(msg =>
        {
            _logger.Debug("Removing subscriber: {0}", msg.SubscriberId);
            _jobManager?.RemoveSubscriber(msg.SubscriberId);
        });

        Receive<GetMetadataEmbeddingStatus>(_ => HandleGetStatusRunning());
    }

    private async Task HandleRegenerateAll(RegenerateAllMetadataEmbeddings msg)
    {
        if (_jobManager != null)
        {
            // Already running a batch, decline new request
            _logger.Warning("Metadata embedding regeneration already in progress, declining new request from {0}", msg.RequestedBy);
            Sender.Tell(new MetadataEmbeddingStatus(
                IsRunning: true,
                Status: "Running",
                TotalProcessed: _jobManager.ProcessedCount,
                TotalSuccessful: _jobManager.SuccessCount,
                TotalFailed: _jobManager.FailureCount,
                Outstanding: _jobManager.TotalItems - _jobManager.ProcessedCount,
                FailedMemoryIds: _jobManager.FailedIds.ToList(),
                StartTime: _jobManager.StartTime,
                Duration: DateTime.UtcNow - _jobManager.StartTime,
                RequestedBy: _jobManager.RequestedBy
            ));
            return;
        }

        _logger.Info("Starting metadata embedding regeneration for ALL memories, page size {0}, requested by {1}",
            msg.PageSize, msg.RequestedBy);

        try
        {
            // Get total count first to size the job
            var (_, totalCount) = await _storage.GetMemoriesPaginated(1, 1);

            // Create job manager and start job
            _jobManager = new ProgressJobManager(_logger, _materializer);
            _jobManager.StartJob(totalCount, msg.RequestedBy);

            // Initialize pagination state
            _currentPage = 1;
            _pageSize = msg.PageSize;

            Become(Running);

            if (totalCount == 0)
            {
                _logger.Info("No memories found to process");
                CompleteBatch();
                return;
            }

            _logger.Info("Found {0} memories to process in pages of {1}", totalCount, _pageSize);

            await ProcessNextPage();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error starting metadata embedding regeneration: {0}", ex.Message);
            _jobManager?.Fail(ex.Message);
            _jobManager = null;
            Become(Idle);
        }
    }

    private async Task ProcessNextPage()
    {
        if (_jobManager == null) return;

        try
        {
            var (memories, _) = await _storage.GetMemoriesPaginated(_currentPage, _pageSize);

            if (memories.Count == 0)
            {
                // No more pages
                CompleteBatch();
                return;
            }

            _logger.Info("Processing {0} memories at page {1}", memories.Count, _currentPage);
            _outstandingOnCurrentPage = memories.Count;

            foreach (var memory in memories)
            {
                Self.Tell(new GenerateMetadataEmbeddingForMemory(
                    memory.Id,
                    memory.Title ?? "Untitled",
                    memory.Tags ?? [],
                    _jobManager.RequestedBy
                ));
            }

            _currentPage++; // Move to next page for next call
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error fetching page {0}: {1}", _currentPage, ex.Message);
            _jobManager.Fail($"Error fetching page {_currentPage}: {ex.Message}");
            _jobManager = null;
            Become(Idle);
        }
    }

    private async Task HandleGenerateMetadataEmbeddingForMemory(GenerateMetadataEmbeddingForMemory msg)
    {
        if (_jobManager == null) return;

        try
        {
            var metadataText = CreateMetadataText(msg.Title, msg.Tags);
            var embeddingArray = await _embeddingService.Generate(metadataText);
            var embedding = new Vector(embeddingArray);
            await _storage.UpdateMemoryMetadataEmbedding(msg.MemoryId, embedding);

            _jobManager.RecordSuccess();
            _logger.Debug("Successfully generated metadata embedding for memory {0}", msg.MemoryId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error generating metadata embedding for memory {0}: {1}", msg.MemoryId, ex.Message);
            _jobManager.RecordFailure(msg.MemoryId);
        }
        finally
        {
            _outstandingOnCurrentPage--;

            if (_outstandingOnCurrentPage == 0)
            {
                // Current page complete, check if we should continue
                if (_jobManager.ProcessedCount >= _jobManager.TotalItems)
                {
                    CompleteBatch();
                }
                else
                {
                    await ProcessNextPage();
                }
            }
        }
    }

    private void CompleteBatch()
    {
        PublishBatchCompleted();

        // Complete the job - this broadcasts final event and auto-completes all subscriber streams
        _jobManager?.Complete();
        _jobManager = null;

        Become(Idle);
    }

    private void PublishBatchCompleted()
    {
        if (_jobManager == null) return;

        var batchCompleted = new BatchMetadataEmbeddingCompleted(
            RequestedBy: _jobManager.RequestedBy,
            StartTime: _jobManager.StartTime,
            TotalProcessed: _jobManager.ProcessedCount,
            TotalSuccessful: _jobManager.SuccessCount,
            FailedMemoryIds: _jobManager.FailedIds.ToList(),
            Duration: DateTime.UtcNow - _jobManager.StartTime
        );

        _logger.Info("Metadata embedding completed: {0}/{1} successful, {2} failed, duration: {3}ms",
            _jobManager.SuccessCount, _jobManager.TotalItems, _jobManager.FailureCount,
            batchCompleted.Duration.TotalMilliseconds);

        Context.System.EventStream.Publish(batchCompleted);
    }

    private void HandleGetStatusIdle()
    {
        Sender.Tell(new MetadataEmbeddingStatus(
            IsRunning: false,
            Status: "idle"
        ));
    }

    private void HandleGetStatusRunning()
    {
        if (_jobManager == null)
        {
            HandleGetStatusIdle();
            return;
        }

        Sender.Tell(new MetadataEmbeddingStatus(
            IsRunning: true,
            Status: "Running",
            TotalProcessed: _jobManager.ProcessedCount,
            TotalSuccessful: _jobManager.SuccessCount,
            TotalFailed: _jobManager.FailureCount,
            Outstanding: _jobManager.TotalItems - _jobManager.ProcessedCount,
            FailedMemoryIds: _jobManager.FailedIds.ToList(),
            StartTime: _jobManager.StartTime,
            Duration: DateTime.UtcNow - _jobManager.StartTime,
            RequestedBy: _jobManager.RequestedBy
        ));
    }

    private static string CreateMetadataText(string title, string[] tags)
    {
        var parts = new List<string> { title };
        if (tags.Length > 0)
        {
            parts.AddRange(tags);
        }
        return string.Join(" ", parts);
    }

    public static Props Props(IStorage storage, IEmbeddingService embeddingService)
    {
        return Akka.Actor.Props.Create(() => new MetadataEmbeddingActor(storage, embeddingService));
    }
}
