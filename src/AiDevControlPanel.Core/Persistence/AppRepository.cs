using AiDevControlPanel.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiDevControlPanel.Core.Persistence;

/// <summary>Detached copy of all data; the GUI and the scheduler work on snapshots.</summary>
public sealed record AppSnapshot(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Tsa> Tsas,
    IReadOnlyList<Issue> Issues,
    IReadOnlyList<IssueDependency> Dependencies)
{
    public static AppSnapshot Empty { get; } = new([], [], [], []);

    public IEnumerable<Tsa> TsasOf(int projectId) => Tsas.Where(t => t.ProjectId == projectId);

    public IEnumerable<Issue> IssuesOf(int projectId) => Issues.Where(i => i.ProjectId == projectId);

    public IEnumerable<Issue> IssuesOfTsa(int tsaId) => Issues.Where(i => i.TsaId == tsaId);

    public IEnumerable<Issue> BlockersOf(int issueId)
    {
        var ids = Dependencies.Where(d => d.IssueId == issueId).Select(d => d.BlockedByIssueId).ToHashSet();
        return Issues.Where(i => ids.Contains(i.Id));
    }

    public IEnumerable<Issue> ChildrenOf(int issueId) => Issues.Where(i => i.ParentIssueId == issueId);
}

public interface IAppRepository
{
    /// <summary>Raised after every write.</summary>
    event EventHandler? Changed;

    Task<AppSnapshot> LoadSnapshotAsync(CancellationToken ct = default);

    Task<Project?> GetProjectAsync(int id, CancellationToken ct = default);

    Task<Tsa?> GetTsaAsync(int id, CancellationToken ct = default);

    Task<Issue?> GetIssueAsync(int id, CancellationToken ct = default);

    Task<Project> AddProjectAsync(Project project, CancellationToken ct = default);

    Task<Tsa> AddTsaAsync(Tsa tsa, CancellationToken ct = default);

    Task<Issue> AddIssueAsync(Issue issue, CancellationToken ct = default);

    Task AddDependencyAsync(int issueId, int blockedByIssueId, CancellationToken ct = default);

    Task<Project> UpdateProjectAsync(int id, Action<Project> mutate, CancellationToken ct = default);

    Task<Tsa> UpdateTsaAsync(int id, Action<Tsa> mutate, CancellationToken ct = default);

    /// <summary>Applies <paramref name="mutate"/> and re-derives the priority.</summary>
    Task<Issue> UpdateIssueAsync(int id, Action<Issue> mutate, CancellationToken ct = default);
}

/// <summary>SQLite storage; every call uses its own context and all calls are serialized.</summary>
public sealed class AppRepository(DbContextOptions<AppDbContext> options, TimeProvider time) : IAppRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(ct);
    }

    public Task<AppSnapshot> LoadSnapshotAsync(CancellationToken ct = default) => ReadAsync(async db => new AppSnapshot(
        await db.Projects.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct),
        await db.Tsas.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct),
        await db.Issues.AsNoTracking().OrderBy(i => i.Id).ToListAsync(ct),
        await db.IssueDependencies.AsNoTracking().ToListAsync(ct)));

    public Task<Project?> GetProjectAsync(int id, CancellationToken ct = default) =>
        ReadAsync(db => db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct));

    public Task<Tsa?> GetTsaAsync(int id, CancellationToken ct = default) =>
        ReadAsync(db => db.Tsas.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct));

    public Task<Issue?> GetIssueAsync(int id, CancellationToken ct = default) =>
        ReadAsync(db => db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct));

    public Task<Project> AddProjectAsync(Project project, CancellationToken ct = default)
    {
        project.CreatedAt = time.GetUtcNow();
        return AddAsync(project, ct);
    }

    public Task<Tsa> AddTsaAsync(Tsa tsa, CancellationToken ct = default)
    {
        tsa.CreatedAt = time.GetUtcNow();
        return AddAsync(tsa, ct);
    }

    public Task<Issue> AddIssueAsync(Issue issue, CancellationToken ct = default)
    {
        issue.CreatedAt = time.GetUtcNow();
        issue.Priority = PriorityRules.Compute(issue);
        return AddAsync(issue, ct);
    }

    public async Task AddDependencyAsync(int issueId, int blockedByIssueId, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            if (!await db.IssueDependencies.AnyAsync(d => d.IssueId == issueId && d.BlockedByIssueId == blockedByIssueId, ct))
            {
                db.IssueDependencies.Add(new IssueDependency { IssueId = issueId, BlockedByIssueId = blockedByIssueId });
                await db.SaveChangesAsync(ct);
            }

            return true;
        });
    }

    public Task<Project> UpdateProjectAsync(int id, Action<Project> mutate, CancellationToken ct = default) =>
        UpdateAsync<Project>(id, mutate, ct);

    public Task<Tsa> UpdateTsaAsync(int id, Action<Tsa> mutate, CancellationToken ct = default) =>
        UpdateAsync<Tsa>(id, mutate, ct);

    public Task<Issue> UpdateIssueAsync(int id, Action<Issue> mutate, CancellationToken ct = default) =>
        UpdateAsync<Issue>(id, issue =>
        {
            mutate(issue);
            issue.Priority = PriorityRules.Compute(issue);
        }, ct);

    private Task<T> AddAsync<T>(T entity, CancellationToken ct) where T : class => WriteAsync(async db =>
    {
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        return entity;
    });

    private Task<T> UpdateAsync<T>(int id, Action<T> mutate, CancellationToken ct) where T : class => WriteAsync(async db =>
    {
        var entity = await db.Set<T>().FindAsync([id], ct) ?? throw new InvalidOperationException($"{typeof(T).Name} {id} not found.");
        mutate(entity);
        await db.SaveChangesAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        return entity;
    });

    private async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = new AppDbContext(options);
            return await read(db);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WriteAsync<T>(Func<AppDbContext, Task<T>> write)
    {
        T result;
        await _gate.WaitAsync();
        try
        {
            await using var db = new AppDbContext(options);
            result = await write(db);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
