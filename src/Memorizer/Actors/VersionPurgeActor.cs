using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Memorizer.Services;

namespace Memorizer.Actors;

/// <summary>
/// Actor responsible for purging old version snapshots.
/// Uses Become/Unbecome to switch between Idle and Running states.
/// Progress is managed via ProgressJobManager which supports multiple SSE subscribers.
/// </summary>
public sealed class VersionPurgeActor : ReceiveActor
{
    private readonly IStorage _storage;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    // Progress manager - handles subscriber management and job state
    private ProgressJobManager? _jobManager;

    public VersionPurgeActor(IStorage storage)
    {
        _storage = storage;
        _logger = Context.GetLogger();
        _materializer = Context.System.Materializer();

        // Start in Idle state
        Idle();
    }

    private void Idle()
    {
        // Idle behavior - waiting for work
        ReceiveAsync<PurgeVersionsByAge>(HandlePurgeVersionsByAge);

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

        Receive<GetVersionPurgeStatus>(_ => HandleGetStatusIdle());
    }

    private void Running()
    {
        // Running behavior - actively processing purge
        // Reject new purge requests while running
        Receive<PurgeVersionsByAge>(msg =>
        {
            _logger.Warning("Version purge already running, rejecting new request from {0}", msg.RequestedBy);
            Sender.Tell(new VersionPurgeStatus(
                IsRunning: true,
                Status: "Already running"
            ));
        });

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

        Receive<GetVersionPurgeStatus>(_ => HandleGetStatusRunning());

        // Handle completion messages
        Receive<BatchVersionPurgeCompleted>(HandleBatchPurgeCompleted);
    }

    private async Task HandlePurgeVersionsByAge(PurgeVersionsByAge message)
    {
        // Capture sender for reply - needed because we're in an async method
        var sender = Sender;
        var self = Self;

        _logger.Info("Starting version purge for versions older than {0} days, requested by {1}",
            message.DaysOld, message.RequestedBy);

        try
        {
            // Calculate cutoff date
            var cutoffDate = DateTime.UtcNow.AddDays(-message.DaysOld);

            // Get version stats to show how many will be affected
            var stats = await _storage.GetVersionStats();

            // Create job manager and start job
            // We'll track this as a single-item job since purge is atomic
            _jobManager = new ProgressJobManager(_logger, _materializer);
            _jobManager.StartJob(1, message.RequestedBy);

            Become(Running);

            // Reply with initial status BEFORE processing starts
            sender.Tell(new VersionPurgeStatus(
                IsRunning: true,
                Status: "Running",
                TotalProcessed: 0,
                TotalSuccessful: 0,
                TotalFailed: 0,
                Outstanding: 1,
                StartTime: _jobManager.StartTime,
                Duration: TimeSpan.Zero,
                RequestedBy: message.RequestedBy
            ));

            // Execute the purge operation
            _logger.Info("Purging versions older than {0} (cutoff: {1})", message.DaysOld, cutoffDate);

            var purgedCount = await _storage.PurgeVersionsOlderThan(cutoffDate);

            _logger.Info("Successfully purged {0} versions", purgedCount);

            // Record success and complete
            var batchCompleted = new BatchVersionPurgeCompleted(
                RequestedBy: message.RequestedBy,
                StartTime: _jobManager.StartTime,
                TotalVersionsPurged: purgedCount,
                TotalEventsPurged: 0, // Events are purged along with versions
                TotalFailed: 0,
                Duration: DateTime.UtcNow - _jobManager.StartTime
            );

            // Send completion to self
            self.Tell(batchCompleted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during version purge: {0}", ex.Message);
            sender.Tell(new VersionPurgeStatus(
                IsRunning: false,
                Status: "Failed: " + ex.Message,
                TotalProcessed: 0,
                TotalSuccessful: 0,
                TotalFailed: 1,
                Outstanding: 0
            ));
            _jobManager?.Fail(ex.Message);
            _jobManager = null;
            Become(Idle);
        }
    }

    private void HandleBatchPurgeCompleted(BatchVersionPurgeCompleted message)
    {
        _logger.Info("Batch version purge completed: {0} versions purged, duration: {1}ms",
            message.TotalVersionsPurged, message.Duration.TotalMilliseconds);

        // Publish to event stream
        Context.System.EventStream.Publish(message);

        // Record success in job manager
        _jobManager?.RecordSuccess();

        // Complete the job - this broadcasts final event and auto-completes all subscriber streams
        _jobManager?.Complete();
        _jobManager = null;

        Become(Idle);
    }

    private void HandleGetStatusIdle()
    {
        Sender.Tell(new VersionPurgeStatus(
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

        Sender.Tell(new VersionPurgeStatus(
            IsRunning: true,
            Status: "Running",
            TotalProcessed: _jobManager.ProcessedCount,
            TotalSuccessful: _jobManager.SuccessCount,
            TotalFailed: _jobManager.FailureCount,
            Outstanding: _jobManager.TotalItems - _jobManager.ProcessedCount,
            StartTime: _jobManager.StartTime,
            Duration: DateTime.UtcNow - _jobManager.StartTime,
            RequestedBy: _jobManager.RequestedBy
        ));
    }

    public static Props Props(IStorage storage)
    {
        return Akka.Actor.Props.Create(() => new VersionPurgeActor(storage));
    }
}
