using Akka;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Memorizer.Services;
using Pgvector;

namespace Memorizer.Actors;

/// <summary>
/// Actor responsible for generating metadata embeddings for all memories using offset/limit pagination
/// </summary>
public sealed class MetadataEmbeddingActor : ReceiveActor
{
    private readonly IStorage _storage;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    private BatchState? _batch;

    // Progress source actor - only exists when running a batch
    private IActorRef? _progressSourceActor;
    private Source<MetadataEmbeddingProgressEvent, NotUsed>? _progressSource;
    
    private enum Status
    {
        Idle,
        Running,
        Completed
    }

    private class BatchState
    {
        public int Outstanding { get; set; }
        public int Success { get; set; }
        public int Failure { get; set; }
        
        public Status CurrentStatus => Outstanding > 0 ? Status.Running : Status.Completed;
        public List<Guid> FailedIds { get; } = new();
        public string RequestedBy { get; set; } = string.Empty;
        public int Page { get; set; } // Start at 1
        public int PageSize { get; set; }
        public DateTime StartTime { get; set; }
    }

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

        // No progress available when idle
        Receive<GetProgressSource<MetadataEmbeddingProgressEvent>>(_ =>
        {
            _logger.Warning("Progress source requested but no batch is running");
            Sender.Tell(new ProgressSource<MetadataEmbeddingProgressEvent>(Source.Empty<MetadataEmbeddingProgressEvent>()));
        });

        Receive<GetMetadataEmbeddingStatus>(_ => HandleGetStatusIdle());
    }

    private void Running()
    {
        // Running behavior - actively processing batch
        ReceiveAsync<GenerateMetadataEmbeddingForMemory>(HandleGenerateMetadataEmbeddingForMemory);

        // Return the active progress source
        Receive<GetProgressSource<MetadataEmbeddingProgressEvent>>(_ =>
        {
            if (_progressSource != null)
            {
                _logger.Debug("Returning active progress source");
                Sender.Tell(new ProgressSource<MetadataEmbeddingProgressEvent>(_progressSource));
            }
        });

        Receive<GetMetadataEmbeddingStatus>(_ => HandleGetStatusRunning());
    }

    private async Task HandleRegenerateAll(RegenerateAllMetadataEmbeddings msg)
    {
        if (_batch is { CurrentStatus: Status.Running })
        {
            // Already running a batch, decline new request
            Sender.Tell(new MetadataEmbeddingStatus(
                IsRunning: true,
                Status: _batch.CurrentStatus.ToString(),
                TotalProcessed: _batch.Success + _batch.Failure,
                TotalSuccessful: _batch.Success,
                TotalFailed: _batch.Failure,
                Outstanding: _batch.Outstanding,
                FailedMemoryIds: _batch.FailedIds.ToList(),
                StartTime: _batch.StartTime,
                Duration: DateTime.UtcNow - _batch.StartTime,
                RequestedBy: _batch.RequestedBy
            ));
            return;
        }
        
        // Clear last completed batch status when starting a new batch
        _logger.Info("Starting metadata embedding regeneration for ALL memories, page size {0}, requested by {1}", msg.PageSize, msg.RequestedBy);
        _batch = new BatchState
        {
            RequestedBy = msg.RequestedBy,
            Page = 1, // Start at page 1
            PageSize = msg.PageSize,
            StartTime = DateTime.UtcNow
        };

        // Create progress source actor and switch to Running state
        var (actorRef, source) = Source.ActorRef<MetadataEmbeddingProgressEvent>(100, OverflowStrategy.DropHead)
            .PreMaterialize(_materializer);

        _progressSourceActor = actorRef;
        _progressSource = source;

        Become(Running);

        // Push initial progress event
        PushProgressUpdate();

        await ProcessNextPage();
    }

    private async Task ProcessNextPage()
    {
        if (_batch == null) return;
        var (memories, _) = await _storage.GetMemoriesPaginated(_batch.Page, _batch.PageSize);
        if (memories.Count == 0)
        {
            CompleteBatch();
            return;
        }

        _logger.Info("Processing {0} memories at page {1}", memories.Count, _batch.Page);
        _batch.Outstanding = memories.Count;
        foreach (var memory in memories)
        {
            Self.Tell(new GenerateMetadataEmbeddingForMemory(
                memory.Id,
                memory.Title ?? "Untitled",
                memory.Tags ?? [],
                _batch.RequestedBy
            ));
        }
        _batch.Page++; // Move to next page for next call
    }

    private async Task HandleGenerateMetadataEmbeddingForMemory(GenerateMetadataEmbeddingForMemory msg)
    {
        if (_batch == null) return;
        try
        {
            var metadataText = CreateMetadataText(msg.Title, msg.Tags);
            var embeddingArray = await _embeddingService.Generate(metadataText);
            var embedding = new Vector(embeddingArray);
            await _storage.UpdateMemoryMetadataEmbedding(msg.MemoryId, embedding);
            _batch.Success++;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error generating metadata embedding for memory {0}: {1}", msg.MemoryId, ex.Message);
            _batch.Failure++;
            _batch.FailedIds.Add(msg.MemoryId);
        }
        finally
        {
            _batch.Outstanding--;
            PushProgressUpdate();

            if (_batch.Outstanding == 0)
            {
                await ProcessNextPage();
            }
        }
    }

    private void PushProgressUpdate()
    {
        if (_progressSourceActor == null || _batch == null) return;

        var progressEvent = new MetadataEmbeddingProgressEvent(
            TotalProcessed: _batch.Success + _batch.Failure,
            TotalSuccessful: _batch.Success,
            TotalFailed: _batch.Failure,
            Outstanding: _batch.Outstanding,
            Status: "Running",
            RequestedBy: _batch.RequestedBy,
            Duration: DateTime.UtcNow - _batch.StartTime,
            FailedMemoryIds: _batch.FailedIds.ToList()
        );

        _progressSourceActor.Tell(progressEvent);
    }

    private void CompleteBatch()
    {
        PublishBatchCompleted();

        // Push final completion event before completing the stream
        if (_progressSourceActor != null && _batch != null)
        {
            var completionEvent = new MetadataEmbeddingProgressEvent(
                TotalProcessed: _batch.Success + _batch.Failure,
                TotalSuccessful: _batch.Success,
                TotalFailed: _batch.Failure,
                Outstanding: 0,
                Status: _batch.Failure > 0 ? "Completed with errors" : "Completed",
                RequestedBy: _batch.RequestedBy,
                Duration: DateTime.UtcNow - _batch.StartTime,
                FailedMemoryIds: _batch.FailedIds.ToList()
            );
            _progressSourceActor.Tell(completionEvent);

            // Now complete the stream
            _progressSourceActor.Tell(new Akka.Actor.Status.Success("Complete"));
        }

        _progressSourceActor = null;
        _progressSource = null;
        Become(Idle);
    }

    private void PublishBatchCompleted()
    {
        if (_batch == null) return;
        var duration = DateTime.UtcNow - _batch.StartTime;
        var batchCompleted = new BatchMetadataEmbeddingCompleted(
            RequestedBy: _batch.RequestedBy,
            StartTime: _batch.StartTime,
            TotalProcessed: _batch.Success + _batch.Failure,
            TotalSuccessful: _batch.Success,
            FailedMemoryIds: _batch.FailedIds.ToList(),
            Duration: duration
        );
        _logger.Info("Metadata embedding completed: {0} successful, {1} failed, duration: {2}ms", _batch.Success, _batch.Failure, duration.TotalMilliseconds);
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
        if (_batch == null) return;

        Sender.Tell(new MetadataEmbeddingStatus(
            IsRunning: _batch.CurrentStatus == Status.Running,
            Status: _batch.CurrentStatus.ToString(),
            TotalProcessed: _batch.Success + _batch.Failure,
            TotalSuccessful: _batch.Success,
            TotalFailed: _batch.Failure,
            Outstanding: _batch.Outstanding,
            FailedMemoryIds: _batch.FailedIds.ToList(),
            StartTime: _batch.StartTime,
            Duration: DateTime.UtcNow - _batch.StartTime,
            RequestedBy: _batch.RequestedBy
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