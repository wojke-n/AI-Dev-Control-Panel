using System.Collections.Concurrent;
using AiDevControlPanel.Core.Domain;
using AiDevControlPanel.Core.Persistence;

namespace AiDevControlPanel.Core.Workflow;

public enum JobKind
{
    Onboard,
    Refine,
    Plan,
    Execute,
}

public sealed record ScheduledJob(JobKind Kind, int ProjectId, int? TsaId = null, int? IssueId = null);

public interface IJobExecutor
{
    Task ExecuteAsync(ScheduledJob job, CancellationToken ct);
}

/// <summary>Decides which job a project runs next (pure, so the ordering rules are unit-testable).</summary>
public static class JobSelector
{
    public static ScheduledJob? Select(AppSnapshot snapshot, Project project, AppSettings settings, DateTimeOffset now)
    {
        if (project.OnboardingState == OnboardingState.Initializing)
        {
            return new ScheduledJob(JobKind.Onboard, project.Id);
        }

        if (project.OnboardingState != OnboardingState.Ready || PausedUntil(snapshot, now) is not null)
        {
            return null;
        }

        var issues = snapshot.IssuesOf(project.Id).Where(i => !i.OnHold && !i.IsContainer).ToList();

        // Started issues first; interrupted ones (priority 7/8) come before the others (5/6).
        var started = issues
            .Where(i => i.State is IssueState.Planning or IssueState.Executing or IssueState.AdjustRequested)
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.Number)
            .FirstOrDefault();
        if (started is not null)
        {
            var kind = started.State == IssueState.Executing ? JobKind.Execute : JobKind.Plan;
            return new ScheduledJob(kind, project.Id, IssueId: started.Id);
        }

        var tsa = snapshot.TsasOf(project.Id).Where(t => t.State is TsaState.Waiting or TsaState.Refining).OrderBy(t => t.Id).FirstOrDefault();
        if (tsa is not null)
        {
            return new ScheduledJob(JobKind.Refine, project.Id, TsaId: tsa.Id);
        }

        if (!settings.AutostartPlanning)
        {
            return null;
        }

        var next = issues.Where(i => i.State == IssueState.Waiting).OrderByDescending(i => i.Priority).ThenBy(i => i.Number).FirstOrDefault();
        return next is null ? null : new ScheduledJob(JobKind.Plan, project.Id, IssueId: next.Id);
    }

    /// <summary>The usage limit is account-wide: while anything waits for a reset, no job starts.</summary>
    public static DateTimeOffset? PausedUntil(AppSnapshot snapshot, DateTimeOffset now)
    {
        var resumeTimes = snapshot.Issues.Where(i => i.IsInterrupted).Select(i => i.ResumeAt)
            .Concat(snapshot.Tsas.Select(t => t.ResumeAt))
            .OfType<DateTimeOffset>()
            .Where(t => t > now)
            .ToList();
        return resumeTimes.Count == 0 ? null : resumeTimes.Max();
    }
}

/// <summary>Starts at most one job (one Claude process) per project; projects run in parallel.</summary>
public sealed class JobScheduler(IAppRepository repo, ISettingsStore settings, IJobExecutor executor, ActivityLog log, TimeProvider time)
    : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, (ScheduledJob Job, Task Task)> _running = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _wakeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event EventHandler? RunningJobsChanged;

    public IReadOnlyList<ScheduledJob> RunningJobs => _running.Values.Select(v => v.Job).ToList();

    public bool IsRunning(int projectId) => _running.ContainsKey(projectId);

    public void Start()
    {
        repo.Changed += OnChanged;
        settings.Changed += OnChanged;
        _loop = LoopAsync(_cts.Token);
    }

    public void Wake()
    {
        lock (_wakeLock)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    /// <summary>Cancels the running jobs; their state stays so they continue after the next start.</summary>
    public async Task StopAsync()
    {
        repo.Changed -= OnChanged;
        settings.Changed -= OnChanged;
        await _cts.CancelAsync();
        var tasks = _running.Values.Select(v => v.Task).ToList();
        if (_loop is not null)
        {
            tasks.Add(_loop);
        }

        await Task.WhenAll(tasks);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            await StopAsync();
        }

        _cts.Dispose();
    }

    internal async Task ScheduleOnceAsync(CancellationToken ct)
    {
        var snapshot = await repo.LoadSnapshotAsync(ct);
        var now = time.GetUtcNow();
        foreach (var project in snapshot.Projects)
        {
            if (_running.ContainsKey(project.Id) || JobSelector.Select(snapshot, project, settings.Current, now) is not { } job)
            {
                continue;
            }

            var started = new TaskCompletionSource();
            _running[project.Id] = (job, RunJobAsync(job, started.Task, ct));
            started.SetResult();
            RunningJobsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScheduleOnceAsync(ct);
                await _wake.WaitAsync(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                log.Add("scheduler", $"✖ {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), time, CancellationToken.None);
            }
        }
    }

    private async Task RunJobAsync(ScheduledJob job, Task registered, CancellationToken ct)
    {
        // Run only after the job is registered, so its first state change cannot schedule it twice.
        await registered;
        await Task.Yield();
        try
        {
            await executor.ExecuteAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.Add("scheduler", $"✖ {job.Kind} failed: {e.Message}");
        }
        finally
        {
            _running.TryRemove(job.ProjectId, out _);
            RunningJobsChanged?.Invoke(this, EventArgs.Empty);
            Wake();
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Wake();
}

public sealed class WorkflowJobExecutor(
    ProjectOnboardingService onboarding,
    RefinerService refiner,
    IssueWorkflowService issues) : IJobExecutor
{
    public Task ExecuteAsync(ScheduledJob job, CancellationToken ct) => job.Kind switch
    {
        JobKind.Onboard => onboarding.RunOnboardingAsync(job.ProjectId, ct),
        JobKind.Refine => refiner.RunAsync(job.TsaId!.Value, ct),
        JobKind.Plan => issues.RunPlanningAsync(job.IssueId!.Value, ct),
        JobKind.Execute => issues.RunExecutionAsync(job.IssueId!.Value, ct),
        _ => Task.CompletedTask,
    };
}
