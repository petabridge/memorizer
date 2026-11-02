using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Memorizer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Memorizer.Actors;

public sealed class MarkdownExportActor : ReceiveActor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggingAdapter _logger;
    private readonly IMaterializer _materializer;

    private ProgressJobManager? _jobManager;
    private IServiceScope? _currentScope;

    public MarkdownExportActor(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = Context.GetLogger();
        _materializer = Context.System.Materializer();

        Idle();
    }

    private void Idle()
    {
        ReceiveAsync<StartMarkdownExport>(HandleStartExport);

        Receive<SubscribeToProgress>(msg =>
        {
            _logger.Debug("Subscription requested while idle, subscriber: {0}", msg.SubscriberId);
            var tempManager = new ProgressJobManager(_logger, _materializer);
            var reader = tempManager.CreateIdleSubscription(msg.SubscriberId);
            Sender.Tell(new ProgressSubscription(msg.SubscriberId, reader));
        });

        Receive<UnsubscribeFromProgress>(msg =>
        {
            _logger.Debug("Unsubscribe requested while idle, subscriber: {0}", msg.SubscriberId);
        });

        Receive<GetMarkdownExportStatus>(_ => HandleGetStatusIdle());
    }

    private void Running()
    {
        Receive<StartMarkdownExport>(msg =>
        {
            _logger.Warning("Markdown export already running, rejecting new request from {0}", msg.RequestedBy);
            Sender.Tell(new MarkdownExportStatus(
                IsRunning: true,
                Status: "Already running"
            ));
        });

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

        Receive<GetMarkdownExportStatus>(_ => HandleGetStatusRunning());
        Receive<MarkdownExportCompleted>(HandleExportCompleted);
    }

    private async Task HandleStartExport(StartMarkdownExport message)
    {
        var sender = Sender;
        var self = Self;

        _logger.Info("Starting markdown export, requested by {0}", message.RequestedBy);

        try
        {
            _currentScope = _serviceProvider.CreateScope();
            var exportService = _currentScope.ServiceProvider.GetRequiredService<IMarkdownExportService>();

            if (!exportService.IsEnabled)
            {
                sender.Tell(new MarkdownExportStatus(
                    IsRunning: false,
                    Status: "Feature disabled - RootPath not configured"
                ));
                _currentScope.Dispose();
                _currentScope = null;
                return;
            }

            _jobManager = new ProgressJobManager(_logger, _materializer);
            _jobManager.StartJob(1, message.RequestedBy);

            Become(Running);

            sender.Tell(new MarkdownExportStatus(
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

            var progress = new Progress<ExportProgress>(p =>
            {
                // Update job manager with progress - we track total items dynamically
                if (p.TotalItems > 0 && _jobManager.TotalItems != p.TotalItems)
                {
                    // Re-initialize with correct total
                    // Note: ProgressJobManager doesn't support changing total mid-job,
                    // but we broadcast via the SSE event
                }
            });

            var result = await exportService.ExportAllAsync(
                message.WorkspaceFilter,
                message.ProjectFilter,
                progress,
                CancellationToken.None);

            var completed = new MarkdownExportCompleted(
                RequestedBy: message.RequestedBy,
                StartTime: _jobManager.StartTime,
                TotalExported: result.TotalExported,
                TotalFailed: result.TotalFailed,
                TotalSkipped: result.TotalSkipped,
                Duration: DateTime.UtcNow - _jobManager.StartTime
            );

            self.Tell(completed);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during markdown export: {0}", ex.Message);
            sender.Tell(new MarkdownExportStatus(
                IsRunning: false,
                Status: "Failed: " + ex.Message
            ));
            _jobManager?.Fail(ex.Message);
            _jobManager = null;
            _currentScope?.Dispose();
            _currentScope = null;
            Become(Idle);
        }
    }

    private void HandleExportCompleted(MarkdownExportCompleted message)
    {
        _logger.Info("Markdown export completed: {0} exported, {1} failed, {2} skipped, duration: {3}ms",
            message.TotalExported, message.TotalFailed, message.TotalSkipped, message.Duration.TotalMilliseconds);

        Context.System.EventStream.Publish(message);

        _jobManager?.RecordSuccess();
        _jobManager?.Complete();
        _jobManager = null;

        _currentScope?.Dispose();
        _currentScope = null;

        Become(Idle);
    }

    private void HandleGetStatusIdle()
    {
        Sender.Tell(new MarkdownExportStatus(
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

        Sender.Tell(new MarkdownExportStatus(
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
}
