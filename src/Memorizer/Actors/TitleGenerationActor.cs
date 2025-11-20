using Akka;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Memorizer.Services;
using Memorizer.Settings;

namespace Memorizer.Actors;

/// <summary>
/// Actor responsible for generating titles for memories that don't have them.
/// Uses Become/Unbecome to switch between Idle and Running states,
/// and pushes progress events to a SourceQueue when running.
/// </summary>
public sealed class TitleGenerationActor : ReceiveActor
{
    private readonly IStorage _storage;
    private readonly ILlmService _llmService;
    private readonly LlmSettings _settings;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    // State for batch processing (only used when Running)
    private DateTime _batchStartTime;
    private string _currentRequestedBy = string.Empty;
    private int _totalToProcess;
    private int _processedCount;
    private int _successCount;
    private int _failureCount;
    private readonly List<Guid> _failedMemoryIds = [];

    // Progress source actor - only exists when running a batch
    private IActorRef? _progressSourceActor;
    private Source<TitleGenerationProgressEvent, NotUsed>? _progressSource;

    public TitleGenerationActor(
        IStorage storage,
        ILlmService llmService,
        LlmSettings settings)
    {
        _storage = storage;
        _llmService = llmService;
        _settings = settings;
        _logger = Context.GetLogger();
        _materializer = Context.System.Materializer();

        // Start in Idle state
        Idle();
    }

    private void Idle()
    {
        // Idle behavior - waiting for work
        ReceiveAsync<GenerateTitlesForUntitled>(HandleGenerateTitlesForUntitled);

        // No progress available when idle
        Receive<GetProgressSource<TitleGenerationProgressEvent>>(_ =>
        {
            _logger.Warning("Progress source requested but no batch is running");
            Sender.Tell(new ProgressSource<TitleGenerationProgressEvent>(Source.Empty<TitleGenerationProgressEvent>()));
        });

        Receive<GetTitleGenerationStatus>(_ => HandleGetStatusIdle());
    }

    private void Running()
    {
        // Running behavior - actively processing batch
        ReceiveAsync<GenerateTitleForMemory>(HandleGenerateTitleForMemory);
        Receive<TitleGenerationCompleted>(HandleTitleGenerationCompleted);
        Receive<TitleGenerationFailed>(HandleTitleGenerationFailed);

        // Return the active progress source
        Receive<GetProgressSource<TitleGenerationProgressEvent>>(_ =>
        {
            if (_progressSource != null)
            {
                _logger.Debug("Returning active progress source");
                Sender.Tell(new ProgressSource<TitleGenerationProgressEvent>(_progressSource));
            }
        });

        Receive<GetTitleGenerationStatus>(_ => HandleGetStatusRunning());
    }

    private async Task HandleGenerateTitlesForUntitled(GenerateTitlesForUntitled message)
    {
        _logger.Info("Starting batch title generation for up to {0} untitled memories, requested by {1}",
            message.BatchSize, message.RequestedBy);

        try
        {
            // Initialize batch state
            _batchStartTime = DateTime.UtcNow;
            _currentRequestedBy = message.RequestedBy;
            _processedCount = 0;
            _successCount = 0;
            _failureCount = 0;
            _failedMemoryIds.Clear();

            // Get memories without titles
            var untitledMemories = await _storage.GetMemoriesWithoutTitles(message.BatchSize);
            _totalToProcess = untitledMemories.Count;

            if (_totalToProcess == 0)
            {
                _logger.Info("No untitled memories found");
                PublishBatchCompleted();
                return;
            }

            _logger.Info("Found {0} untitled memories to process", _totalToProcess);

            // Create progress source actor and switch to Running state
            var (actorRef, source) = Source.ActorRef<TitleGenerationProgressEvent>(100, OverflowStrategy.DropHead)
                .PreMaterialize(_materializer);

            _progressSourceActor = actorRef;
            _progressSource = source;

            Become(Running);

            // Push initial progress event
            PushProgressUpdate();

            // Process each memory
            foreach (var memory in untitledMemories)
            {
                var generateMessage = new GenerateTitleForMemory
                {
                    MemoryId = memory.Id,
                    Content = memory.Text,
                    Type = memory.Type,
                    Tags = memory.Tags,
                    RequestedBy = message.RequestedBy
                };

                // Send to self for processing
                Self.Tell(generateMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error starting batch title generation: {0}", ex.Message);
            CompleteBatch();
        }
    }

    private async Task HandleGenerateTitleForMemory(GenerateTitleForMemory message)
    {
        _logger.Debug("Generating title for memory {0}", message.MemoryId);

        try
        {
            // Generate title using LLM service
            var title = await _llmService.GenerateTitle(
                message.Content,
                message.Type,
                message.Tags,
                maxTitleLength: 80);

            // Update the memory with the generated title
            await _storage.UpdateMemoryTitle(message.MemoryId, title);

            _logger.Debug("Successfully generated title '{0}' for memory {1}", title, message.MemoryId);

            var completed = new TitleGenerationCompleted
            {
                MemoryId = message.MemoryId,
                GeneratedTitle = title,
                RequestedBy = message.RequestedBy
            };

            // Publish completion event
            Context.System.EventStream.Publish(completed);
            
            // Handle completion in batch context
            Self.Tell(completed);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error generating title for memory {0}: {1}", message.MemoryId, ex.Message);

            var failed = new TitleGenerationFailed
            {
                MemoryId = message.MemoryId,
                ErrorMessage = ex.Message,
                RequestedBy = message.RequestedBy,
                Exception = ex
            };

            // Publish failure event
            Context.System.EventStream.Publish(failed);
            
            // Handle failure in batch context
            Self.Tell(failed);
        }
    }

    private void HandleTitleGenerationCompleted(TitleGenerationCompleted message)
    {
        _processedCount++;
        _successCount++;

        _logger.Debug("Title generation completed for memory {0} ({1}/{2})",
            message.MemoryId, _processedCount, _totalToProcess);

        PushProgressUpdate();
        CheckBatchCompletion();
    }

    private void HandleTitleGenerationFailed(TitleGenerationFailed message)
    {
        _processedCount++;
        _failureCount++;
        _failedMemoryIds.Add(message.MemoryId);

        _logger.Warning("Title generation failed for memory {0} ({1}/{2}): {3}",
            message.MemoryId, _processedCount, _totalToProcess, message.ErrorMessage);

        PushProgressUpdate();
        CheckBatchCompletion();
    }

    private void CheckBatchCompletion()
    {
        if (_processedCount >= _totalToProcess)
        {
            CompleteBatch();
        }
    }

    private void PushProgressUpdate()
    {
        if (_progressSourceActor == null) return;

        var outstanding = _totalToProcess - _processedCount;
        var progressEvent = new TitleGenerationProgressEvent(
            TotalProcessed: _processedCount,
            TotalSuccessful: _successCount,
            TotalFailed: _failureCount,
            Outstanding: outstanding,
            Status: "Running",
            RequestedBy: _currentRequestedBy,
            Duration: DateTime.UtcNow - _batchStartTime,
            FailedMemoryIds: _failedMemoryIds.ToList()
        );

        _progressSourceActor.Tell(progressEvent);
    }

    private void CompleteBatch()
    {
        PublishBatchCompleted();
        _progressSourceActor?.Tell(new Akka.Actor.Status.Success("Complete"));
        _progressSourceActor = null;
        _progressSource = null;
        Become(Idle);
    }

    private void PublishBatchCompleted()
    {
        var duration = DateTime.UtcNow - _batchStartTime;
        var batchCompleted = new BatchTitleGenerationCompleted(
            RequestedBy: _currentRequestedBy,
            StartTime: _batchStartTime,
            TotalProcessed: _processedCount,
            TotalSuccessful: _successCount,
            FailedMemoryIds: _failedMemoryIds.ToList(),
            Duration: duration
        );

        _logger.Info("Batch title generation completed: {0}/{1} successful, {2} failed, duration: {3}ms",
            _successCount, _totalToProcess, _failureCount,
            duration.TotalMilliseconds);

        Context.System.EventStream.Publish(batchCompleted);
    }

    private void HandleGetStatusIdle()
    {
        Sender.Tell(new TitleGenerationStatus(
            IsRunning: false,
            Status: "idle"
        ));
    }

    private void HandleGetStatusRunning()
    {
        var outstanding = _totalToProcess - _processedCount;
        Sender.Tell(new TitleGenerationStatus(
            IsRunning: true,
            Status: "Running",
            TotalProcessed: _processedCount,
            TotalSuccessful: _successCount,
            TotalFailed: _failureCount,
            Outstanding: outstanding,
            FailedMemoryIds: _failedMemoryIds.ToList(),
            StartTime: _batchStartTime,
            Duration: DateTime.UtcNow - _batchStartTime,
            RequestedBy: _currentRequestedBy
        ));
    }

    public static Props Props(IStorage storage, ILlmService llmService, LlmSettings settings)
    {
        return Akka.Actor.Props.Create(() => new TitleGenerationActor(storage, llmService, settings));
    }
} 