using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Memorizer.Actors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Memorizer.IntegrationTests;

/// <summary>
/// Tests for ProgressJobManager state machine behavior.
/// Tests the three phases: Job Sizing, Reporting, and Completion.
/// </summary>
public class ProgressJobManagerTests : TestKit
{
    private readonly ITestOutputHelper _output;

    public ProgressJobManagerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // No additional services needed for these tests
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No additional Akka configuration needed
    }

    private ProgressJobManager CreateJobManager()
    {
        var logger = Sys.Log;
        var materializer = Sys.Materializer();
        return new ProgressJobManager(logger, materializer);
    }

    #region Job Sizing Phase Tests

    [Fact]
    public void StartJob_SetsCorrectInitialState()
    {
        // Arrange
        var manager = CreateJobManager();

        // Act
        manager.StartJob(totalItems: 10, requestedBy: "test-user");

        // Assert
        Assert.Equal(10, manager.TotalItems);
        Assert.Equal(0, manager.ProcessedCount);
        Assert.Equal(0, manager.SuccessCount);
        Assert.Equal(0, manager.FailureCount);
        Assert.Equal("test-user", manager.RequestedBy);
        Assert.Equal(JobStatus.Running, manager.CurrentStatus);
        _output.WriteLine("StartJob correctly initialized state");
    }

    [Fact]
    public void StartJob_WithZeroItems_SetsNoWorkToDoStatus()
    {
        // Arrange
        var manager = CreateJobManager();

        // Act
        manager.StartJob(totalItems: 0, requestedBy: "test-user");

        // Assert
        Assert.Equal(0, manager.TotalItems);
        Assert.Equal(JobStatus.NoWorkToDo, manager.CurrentStatus);
        _output.WriteLine("StartJob with 0 items correctly sets NoWorkToDo status");
    }

    [Fact]
    public async Task StartJob_BroadcastsToExistingSubscribers()
    {
        // Arrange - Start the job FIRST, then add subscriber
        // In real usage, job manager is created when job starts, so subscribers join after
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 5, requestedBy: "test-user");

        // Now add subscriber - they should get current Running status
        var reader = manager.AddSubscriber("sub-1");

        // Complete the job so the stream closes
        for (int i = 0; i < 5; i++) manager.RecordSuccess();
        manager.Complete();

        // Assert - collect all events
        var events = new List<ProgressEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
            _output.WriteLine($"Received event: Status={evt.Status}, TotalItems={evt.TotalItems}, Processed={evt.TotalProcessed}");
        }

        Assert.True(events.Count >= 1, $"Expected at least 1 event, got {events.Count}");

        // First event should be Running with 5 items (current status when joined)
        var firstEvent = events.First();
        Assert.Equal(JobStatus.Running, firstEvent.Status);
        Assert.Equal(5, firstEvent.TotalItems);
        Assert.Equal("test-user", firstEvent.RequestedBy);

        _output.WriteLine("StartJob correctly broadcast to existing subscribers");
    }

    #endregion

    #region Reporting Phase Tests

    [Fact]
    public void RecordSuccess_IncrementsCounters()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 5, requestedBy: "test-user");

        // Act
        manager.RecordSuccess();
        manager.RecordSuccess();

        // Assert
        Assert.Equal(2, manager.ProcessedCount);
        Assert.Equal(2, manager.SuccessCount);
        Assert.Equal(0, manager.FailureCount);
        Assert.Empty(manager.FailedIds);
        _output.WriteLine("RecordSuccess correctly increments counters");
    }

    [Fact]
    public void RecordFailure_IncrementsCountersAndTracksFailedId()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 5, requestedBy: "test-user");
        var failedId1 = Guid.NewGuid();
        var failedId2 = Guid.NewGuid();

        // Act
        manager.RecordSuccess();
        manager.RecordFailure(failedId1);
        manager.RecordFailure(failedId2);

        // Assert
        Assert.Equal(3, manager.ProcessedCount);
        Assert.Equal(1, manager.SuccessCount);
        Assert.Equal(2, manager.FailureCount);
        Assert.Equal(2, manager.FailedIds.Count);
        Assert.Contains(failedId1, manager.FailedIds);
        Assert.Contains(failedId2, manager.FailedIds);
        _output.WriteLine("RecordFailure correctly increments counters and tracks failed IDs");
    }

    [Fact]
    public async Task RecordSuccess_BroadcastsProgressToSubscribers()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 3, requestedBy: "test-user");
        var reader = manager.AddSubscriber("sub-1");

        // Act - process all items to completion
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.Complete();

        // Assert - collect all events
        var events = new List<ProgressEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
            _output.WriteLine($"Received: Processed={evt.TotalProcessed}/{evt.TotalItems}, Status={evt.Status}");
        }

        // Should have received: initial status (0/3) + 3 progress updates (1/3, 2/3, 3/3) + completion
        Assert.True(events.Count >= 4, $"Expected at least 4 events, got {events.Count}");

        // Verify progress increments - first Running event is initial (0 processed), then 1, 2, 3
        var progressEvents = events.Where(e => e.Status == JobStatus.Running).ToList();
        Assert.True(progressEvents.Count >= 4, $"Expected at least 4 Running events, got {progressEvents.Count}");

        // Check that we see the progression: 0 -> 1 -> 2 -> 3
        Assert.Equal(0, progressEvents[0].TotalProcessed); // Initial status
        Assert.Equal(1, progressEvents[1].TotalProcessed); // After first RecordSuccess
        Assert.Equal(2, progressEvents[2].TotalProcessed); // After second RecordSuccess
        Assert.Equal(3, progressEvents[3].TotalProcessed); // After third RecordSuccess

        _output.WriteLine("RecordSuccess correctly broadcasts progress to subscribers");
    }

    [Fact]
    public void CreateCurrentEvent_CalculatesOutstandingCorrectly()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 10, requestedBy: "test-user");
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.RecordFailure(Guid.NewGuid());

        // Act
        var evt = manager.CreateCurrentEvent();

        // Assert
        Assert.Equal(10, evt.TotalItems);
        Assert.Equal(3, evt.TotalProcessed);
        Assert.Equal(2, evt.TotalSuccessful);
        Assert.Equal(1, evt.TotalFailed);
        Assert.Equal(7, evt.Outstanding);
        Assert.Equal(30, evt.PercentComplete); // 3/10 = 30%
        _output.WriteLine($"CreateCurrentEvent: {evt.TotalProcessed}/{evt.TotalItems} = {evt.PercentComplete}%");
    }

    #endregion

    #region Completion Phase Tests

    [Fact]
    public void Complete_WithAllSuccess_SetsCompletedSuccessStatus()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 3, requestedBy: "test-user");
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.RecordSuccess();

        // Act
        manager.Complete();

        // Assert
        Assert.Equal(JobStatus.CompletedSuccess, manager.CurrentStatus);
        _output.WriteLine("Complete with all success sets CompletedSuccess status");
    }

    [Fact]
    public void Complete_WithSomeFailures_SetsCompletedWithErrorsStatus()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 3, requestedBy: "test-user");
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.RecordFailure(Guid.NewGuid());

        // Act
        manager.Complete();

        // Assert
        Assert.Equal(JobStatus.CompletedWithErrors, manager.CurrentStatus);
        _output.WriteLine("Complete with some failures sets CompletedWithErrors status");
    }

    [Fact]
    public void Fail_SetsFailedStatus()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 10, requestedBy: "test-user");
        manager.RecordSuccess();
        manager.RecordSuccess();

        // Act - job fails early (only 2/10 processed)
        manager.Fail("Database connection lost");

        // Assert
        Assert.Equal(JobStatus.Failed, manager.CurrentStatus);
        Assert.Equal(2, manager.ProcessedCount);
        _output.WriteLine("Fail correctly sets Failed status and preserves progress");
    }

    [Fact]
    public async Task Complete_ClosesAllSubscriberStreams()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 2, requestedBy: "test-user");
        var reader1 = manager.AddSubscriber("sub-1");
        var reader2 = manager.AddSubscriber("sub-2");

        // Act
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.Complete();

        // Assert - both streams should complete
        var events1 = new List<ProgressEvent>();
        var events2 = new List<ProgressEvent>();

        await foreach (var evt in reader1.ReadAllAsync())
        {
            events1.Add(evt);
        }

        await foreach (var evt in reader2.ReadAllAsync())
        {
            events2.Add(evt);
        }

        Assert.NotEmpty(events1);
        Assert.NotEmpty(events2);
        Assert.Equal(JobStatus.CompletedSuccess, events1.Last().Status);
        Assert.Equal(JobStatus.CompletedSuccess, events2.Last().Status);

        _output.WriteLine($"Both subscriber streams closed after completion. Sub1: {events1.Count} events, Sub2: {events2.Count} events");
    }

    [Fact]
    public async Task Fail_ClosesAllSubscriberStreams()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 10, requestedBy: "test-user");
        var reader = manager.AddSubscriber("sub-1");
        manager.RecordSuccess();

        // Act
        manager.Fail("Test failure");

        // Assert - stream should complete with Failed status
        var events = new List<ProgressEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
        }

        Assert.NotEmpty(events);
        Assert.Equal(JobStatus.Failed, events.Last().Status);
        _output.WriteLine("Subscriber stream closed after Fail()");
    }

    #endregion

    #region Idle Subscription Tests

    [Fact]
    public async Task CreateIdleSubscription_ReturnsImmediatelyCompletingStream()
    {
        // Arrange
        var manager = CreateJobManager();

        // Act
        var reader = manager.CreateIdleSubscription("idle-sub");

        // Assert - should receive one Idle event and then complete
        var events = new List<ProgressEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
            _output.WriteLine($"Received idle event: Status={evt.Status}");
        }

        Assert.Single(events);
        Assert.Equal(JobStatus.Idle, events[0].Status);
        Assert.Equal(0, events[0].TotalItems);
        _output.WriteLine("CreateIdleSubscription returns immediately completing stream with Idle status");
    }

    [Fact]
    public async Task CreateIdleSubscription_DoesNotAddToSubscriberList()
    {
        // Arrange
        var manager = CreateJobManager();

        // Act
        var reader = manager.CreateIdleSubscription("idle-sub");

        // Consume the stream
        await foreach (var _ in reader.ReadAllAsync()) { }

        // Assert
        Assert.Equal(0, manager.SubscriberCount);
        _output.WriteLine("CreateIdleSubscription does not add to subscriber list");
    }

    #endregion

    #region Multi-Subscriber Tests

    [Fact]
    public async Task MultipleSubscribers_AllReceiveSameEvents()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 2, requestedBy: "test-user");

        var reader1 = manager.AddSubscriber("sub-1");
        var reader2 = manager.AddSubscriber("sub-2");
        var reader3 = manager.AddSubscriber("sub-3");

        // Act
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.Complete();

        // Assert - all readers should receive the same events
        var task1 = CollectAllEvents(reader1);
        var task2 = CollectAllEvents(reader2);
        var task3 = CollectAllEvents(reader3);

        var results = await Task.WhenAll(task1, task2, task3);

        var events1 = results[0];
        var events2 = results[1];
        var events3 = results[2];

        // All should have same number of events
        Assert.Equal(events1.Count, events2.Count);
        Assert.Equal(events2.Count, events3.Count);

        // All should end with completion
        Assert.Equal(JobStatus.CompletedSuccess, events1.Last().Status);
        Assert.Equal(JobStatus.CompletedSuccess, events2.Last().Status);
        Assert.Equal(JobStatus.CompletedSuccess, events3.Last().Status);

        _output.WriteLine($"All 3 subscribers received {events1.Count} events each");
    }

    [Fact]
    public async Task LateSubscriber_ReceivesCurrentStatusOnJoin()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 5, requestedBy: "test-user");
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.RecordSuccess();
        // Job is now 3/5 complete

        // Act - late subscriber joins
        var lateReader = manager.AddSubscriber("late-sub");

        // Complete the job
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.Complete();

        // Assert
        var events = await CollectAllEvents(lateReader);

        // First event for late subscriber should show 3/5 complete (current state when they joined)
        var firstEvent = events.First();
        Assert.Equal(3, firstEvent.TotalProcessed);
        Assert.Equal(5, firstEvent.TotalItems);
        Assert.Equal(JobStatus.Running, firstEvent.Status);

        // Last event should be completion
        Assert.Equal(JobStatus.CompletedSuccess, events.Last().Status);
        Assert.Equal(5, events.Last().TotalProcessed);

        _output.WriteLine($"Late subscriber joined at 3/5, received {events.Count} events, ended at completion");
    }

    [Fact]
    public void RemoveSubscriber_RemovesFromList()
    {
        // Arrange
        var manager = CreateJobManager();
        manager.StartJob(totalItems: 5, requestedBy: "test-user");
        manager.AddSubscriber("sub-1");
        manager.AddSubscriber("sub-2");
        Assert.Equal(2, manager.SubscriberCount);

        // Act
        manager.RemoveSubscriber("sub-1");

        // Assert
        Assert.Equal(1, manager.SubscriberCount);
        _output.WriteLine("RemoveSubscriber correctly removes from subscriber list");
    }

    #endregion

    #region Helper Methods

    private static async Task<List<ProgressEvent>> CollectAllEvents(System.Threading.Channels.ChannelReader<ProgressEvent> reader)
    {
        var events = new List<ProgressEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
        }
        return events;
    }

    #endregion
}
