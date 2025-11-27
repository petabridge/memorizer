using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;

namespace Memorizer.Actors;

/// <summary>
/// Manages progress tracking and multi-subscriber broadcasting for background job actors.
/// Uses Source.ActorRef pattern - each subscriber gets a materialized source, we keep the actor refs.
/// </summary>
public sealed class ProgressJobManager
{
    private readonly Dictionary<string, IActorRef> _subscriberSources = new();
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    // Job state
    public int TotalItems { get; private set; }
    public int ProcessedCount { get; private set; }
    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public DateTime StartTime { get; private set; }
    public List<Guid> FailedIds { get; } = new();
    public JobStatus CurrentStatus { get; private set; } = JobStatus.Idle;

    public ProgressJobManager(ILoggingAdapter logger, IMaterializer materializer)
    {
        _logger = logger;
        _materializer = materializer;
    }

    /// <summary>
    /// Start a new job. Must be called before processing begins.
    /// This "sizes" the job by setting the total item count.
    /// </summary>
    public void StartJob(int totalItems, string requestedBy)
    {
        TotalItems = totalItems;
        ProcessedCount = 0;
        SuccessCount = 0;
        FailureCount = 0;
        RequestedBy = requestedBy;
        StartTime = DateTime.UtcNow;
        FailedIds.Clear();
        CurrentStatus = totalItems > 0 ? JobStatus.Running : JobStatus.NoWorkToDo;

        _logger.Info("Started job with {0} items, requested by {1}", totalItems, requestedBy);

        // Broadcast initial status to any existing subscribers
        Broadcast(CreateCurrentEvent());
    }

    /// <summary>
    /// Add a subscriber. Materializes a new Source.ActorRef for this subscriber,
    /// stores the actor ref, and returns a ChannelReader for SSE consumption.
    /// Auto-pushes current status to the new subscriber.
    /// </summary>
    public ChannelReader<ProgressEvent> AddSubscriber(string subscriberId)
    {
        // Create the stream pipeline:
        // Source.ActorRef -> AutoCompleteOnFinished -> Sink.ChannelReader (materializes to ChannelReader)
        // When the stream completes (via AutoCompleteOnFinished), the channel is automatically completed
        var (sourceActorRef, channelReader) = Source.ActorRef<ProgressEvent>(
                bufferSize: 100,
                overflowStrategy: OverflowStrategy.DropHead)
            .Via(new AutoCompleteOnFinished())
            .ToMaterialized(
                Sink.ChannelReader<ProgressEvent>(bufferSize: 100, singleReader: true),
                Keep.Both)
            .Run(_materializer);

        _subscriberSources[subscriberId] = sourceActorRef;
        _logger.Debug("Added subscriber {0}, total subscribers: {1}", subscriberId, _subscriberSources.Count);

        // Auto-push current status to the new subscriber
        sourceActorRef.Tell(CreateCurrentEvent());

        return channelReader;
    }

    /// <summary>
    /// Remove a subscriber. The stream may already be closed by the client disconnect.
    /// </summary>
    public void RemoveSubscriber(string subscriberId)
    {
        if (_subscriberSources.Remove(subscriberId))
        {
            _logger.Debug("Removed subscriber {0}, remaining: {1}", subscriberId, _subscriberSources.Count);
        }
    }

    /// <summary>
    /// Record a successful item processing. Increments counters and broadcasts to all subscribers.
    /// </summary>
    public void RecordSuccess()
    {
        ProcessedCount++;
        SuccessCount++;
        Broadcast(CreateCurrentEvent());
    }

    /// <summary>
    /// Record a failed item processing. Increments counters, records the failed ID, and broadcasts.
    /// </summary>
    public void RecordFailure(Guid failedId)
    {
        ProcessedCount++;
        FailureCount++;
        FailedIds.Add(failedId);
        Broadcast(CreateCurrentEvent());
    }

    /// <summary>
    /// Update progress from an external source (e.g., piggybacking on another actor's progress).
    /// Use this when this job manager is forwarding progress from another actor's work.
    /// </summary>
    public void ReportProgress(int processedCount, int totalItems, int successCount, int failureCount, string? statusMessage = null)
    {
        ProcessedCount = processedCount;
        TotalItems = totalItems;
        SuccessCount = successCount;
        FailureCount = failureCount;

        var evt = new ProgressEvent(
            TotalItems: TotalItems,
            TotalProcessed: ProcessedCount,
            TotalSuccessful: SuccessCount,
            TotalFailed: FailureCount,
            Outstanding: TotalItems - ProcessedCount,
            Status: CurrentStatus,
            RequestedBy: RequestedBy,
            Duration: DateTime.UtcNow - StartTime,
            FailedIds: FailedIds.Count > 0 ? FailedIds.ToList() : null,
            Message: statusMessage
        );

        Broadcast(evt);
    }

    /// <summary>
    /// Complete the job successfully. Sets final status and broadcasts completion event.
    /// The AutoCompleteOnFinished stage will gracefully close all subscriber streams.
    /// </summary>
    public void Complete()
    {
        CurrentStatus = DetermineCompletionStatus();
        var completionEvent = CreateCurrentEvent();

        _logger.Info("Job completed: {0} processed, {1} successful, {2} failed, status: {3}",
            ProcessedCount, SuccessCount, FailureCount, CurrentStatus);

        Broadcast(completionEvent);

        // Clear subscribers - streams will auto-close via the AutoCompleteOnFinished stage
        _subscriberSources.Clear();
    }

    /// <summary>
    /// Fail the job early. Sets status to Failed and broadcasts final event.
    /// Use this when the job encounters an unrecoverable error before completing all items.
    /// </summary>
    public void Fail(string? reason = null)
    {
        CurrentStatus = JobStatus.Failed;
        var failureEvent = CreateCurrentEvent();

        _logger.Warning("Job failed early: {0} processed, {1} successful, {2} failed. Reason: {3}",
            ProcessedCount, SuccessCount, FailureCount, reason ?? "Unknown");

        Broadcast(failureEvent);

        // Clear subscribers - streams will auto-close via the AutoCompleteOnFinished stage
        _subscriberSources.Clear();
    }

    /// <summary>
    /// Create an idle subscription that immediately sends an Idle status and completes.
    /// Use this when a subscriber connects but no job is running.
    /// </summary>
    public ChannelReader<ProgressEvent> CreateIdleSubscription(string subscriberId)
    {
        // Create the stream pipeline same as AddSubscriber
        var (sourceActorRef, channelReader) = Source.ActorRef<ProgressEvent>(
                bufferSize: 10,
                overflowStrategy: OverflowStrategy.DropHead)
            .Via(new AutoCompleteOnFinished())
            .ToMaterialized(
                Sink.ChannelReader<ProgressEvent>(bufferSize: 10, singleReader: true),
                Keep.Both)
            .Run(_materializer);

        _logger.Debug("Created idle subscription for {0}", subscriberId);

        // Send idle status - this is a completion status, so AutoCompleteOnFinished will close the stream
        var idleEvent = new ProgressEvent(
            TotalItems: 0,
            TotalProcessed: 0,
            TotalSuccessful: 0,
            TotalFailed: 0,
            Outstanding: 0,
            Status: JobStatus.Idle,
            RequestedBy: string.Empty,
            Duration: null,
            FailedIds: null
        );
        sourceActorRef.Tell(idleEvent);

        // Note: We don't add to _subscriberSources since this is a one-shot subscription
        return channelReader;
    }

    /// <summary>
    /// Create a progress event from current state.
    /// </summary>
    public ProgressEvent CreateCurrentEvent()
    {
        return new ProgressEvent(
            TotalItems: TotalItems,
            TotalProcessed: ProcessedCount,
            TotalSuccessful: SuccessCount,
            TotalFailed: FailureCount,
            Outstanding: TotalItems - ProcessedCount,
            Status: CurrentStatus,
            RequestedBy: RequestedBy,
            Duration: DateTime.UtcNow - StartTime,
            FailedIds: FailedIds.Count > 0 ? FailedIds.ToList() : null
        );
    }

    /// <summary>
    /// Broadcast a progress event to all subscribers.
    /// </summary>
    private void Broadcast(ProgressEvent evt)
    {
        foreach (var (_, sourceActorRef) in _subscriberSources)
        {
            sourceActorRef.Tell(evt);
        }
    }

    private JobStatus DetermineCompletionStatus()
    {
        if (TotalItems == 0) return JobStatus.NoWorkToDo;
        if (FailureCount > 0) return JobStatus.CompletedWithErrors;
        return JobStatus.CompletedSuccess;
    }

    public int SubscriberCount => _subscriberSources.Count;
    public bool HasSubscribers => _subscriberSources.Count > 0;
}

/// <summary>
/// Custom Akka.Streams GraphStage that passes through all ProgressEvent messages,
/// and when it sees a completion status, emits the event then gracefully completes the stream.
/// </summary>
public sealed class AutoCompleteOnFinished : GraphStage<FlowShape<ProgressEvent, ProgressEvent>>
{
    public Inlet<ProgressEvent> In { get; } = new("AutoCompleteOnFinished.in");
    public Outlet<ProgressEvent> Out { get; } = new("AutoCompleteOnFinished.out");

    public override FlowShape<ProgressEvent, ProgressEvent> Shape => new(In, Out);

    protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
    {
        return new Logic(this);
    }

    private sealed class Logic : GraphStageLogic
    {
        public Logic(AutoCompleteOnFinished stage) : base(stage.Shape)
        {
            SetHandler(stage.In, onPush: () =>
            {
                var element = Grab(stage.In);
                Push(stage.Out, element);

                // If this is a completion event, gracefully complete the stream
                if (element.IsCompleted)
                {
                    CompleteStage();
                }
            });

            SetHandler(stage.Out, onPull: () =>
            {
                Pull(stage.In);
            });
        }
    }
}
