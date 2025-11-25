using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Memorizer.Services;
using Pgvector;

namespace Memorizer.Actors;

/// <summary>
/// Actor responsible for regenerating ALL embeddings (content + metadata) for all memories.
/// Used when embedding dimensions or model configuration changes.
/// Uses Become/Unbecome to switch between Idle and Running states.
/// Progress is managed via ProgressJobManager which supports multiple SSE subscribers.
/// </summary>
public sealed class EmbeddingRegenerationActor : ReceiveActor
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

    public EmbeddingRegenerationActor(
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
        ReceiveAsync<RegenerateAllEmbeddings>(HandleRegenerateAll);

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

        Receive<GetEmbeddingRegenerationStatus>(_ => HandleGetStatusIdle());
    }

    private void Running()
    {
        // Running behavior - actively processing batch
        ReceiveAsync<RegenerateEmbeddingsForMemory>(HandleRegenerateEmbeddingsForMemory);

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

        Receive<GetEmbeddingRegenerationStatus>(_ => HandleGetStatusRunning());
    }

    private async Task HandleRegenerateAll(RegenerateAllEmbeddings msg)
    {
        // Capture sender for reply - needed because we're in an async method
        var sender = Sender;

        if (_jobManager != null)
        {
            // Already running a batch, decline new request
            _logger.Warning("Embedding regeneration already in progress, declining new request from {0}", msg.RequestedBy);
            sender.Tell(new EmbeddingRegenerationStatus(
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

        _logger.Info("Starting embedding regeneration for ALL memories, page size {0}, requested by {1}",
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

            // Reply with initial status BEFORE processing starts - this ensures
            // the HTTP request completes and SSE can connect while job is Running
            sender.Tell(new EmbeddingRegenerationStatus(
                IsRunning: true,
                Status: "Running",
                TotalProcessed: 0,
                TotalSuccessful: 0,
                TotalFailed: 0,
                Outstanding: totalCount,
                FailedMemoryIds: [],
                StartTime: _jobManager.StartTime,
                Duration: TimeSpan.Zero,
                RequestedBy: msg.RequestedBy
            ));

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
            _logger.Error(ex, "Error starting embedding regeneration: {0}", ex.Message);
            sender.Tell(new EmbeddingRegenerationStatus(
                IsRunning: false,
                Status: "Failed",
                TotalProcessed: 0,
                TotalSuccessful: 0,
                TotalFailed: 0,
                Outstanding: 0
            ));
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
                Self.Tell(new RegenerateEmbeddingsForMemory(
                    memory.Id,
                    memory.Title ?? "Untitled",
                    memory.Text,
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

    private async Task HandleRegenerateEmbeddingsForMemory(RegenerateEmbeddingsForMemory msg)
    {
        if (_jobManager == null) return;

        try
        {
            // Generate content embedding (title + text)
            var contentText = CreateContentText(msg.Title, msg.Text);
            var contentEmbeddingArray = await _embeddingService.Generate(contentText);
            var contentEmbedding = new Vector(contentEmbeddingArray);

            // Generate metadata embedding (title + tags)
            var metadataText = CreateMetadataText(msg.Title, msg.Tags);
            var metadataEmbeddingArray = await _embeddingService.Generate(metadataText);
            var metadataEmbedding = new Vector(metadataEmbeddingArray);

            // Update both embeddings in a single DB call
            await _storage.UpdateMemoryEmbeddings(msg.MemoryId, contentEmbedding, metadataEmbedding);

            _jobManager.RecordSuccess();
            _logger.Debug("Successfully regenerated embeddings for memory {0}", msg.MemoryId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error regenerating embeddings for memory {0}: {1}", msg.MemoryId, ex.Message);
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

        var batchCompleted = new BatchEmbeddingRegenerationCompleted(
            RequestedBy: _jobManager.RequestedBy,
            StartTime: _jobManager.StartTime,
            TotalProcessed: _jobManager.ProcessedCount,
            TotalSuccessful: _jobManager.SuccessCount,
            FailedMemoryIds: _jobManager.FailedIds.ToList(),
            Duration: DateTime.UtcNow - _jobManager.StartTime
        );

        _logger.Info("Embedding regeneration completed: {0}/{1} successful, {2} failed, duration: {3}ms",
            _jobManager.SuccessCount, _jobManager.TotalItems, _jobManager.FailureCount,
            batchCompleted.Duration.TotalMilliseconds);

        Context.System.EventStream.Publish(batchCompleted);
    }

    private void HandleGetStatusIdle()
    {
        Sender.Tell(new EmbeddingRegenerationStatus(
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

        Sender.Tell(new EmbeddingRegenerationStatus(
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

    /// <summary>
    /// Creates content text for embedding: title + full text content
    /// </summary>
    private static string CreateContentText(string title, string text)
    {
        return string.IsNullOrWhiteSpace(title) ? text : $"{title} {text}";
    }

    /// <summary>
    /// Creates metadata text for embedding: title + tags
    /// </summary>
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
        return Akka.Actor.Props.Create(() => new EmbeddingRegenerationActor(storage, embeddingService));
    }
}
