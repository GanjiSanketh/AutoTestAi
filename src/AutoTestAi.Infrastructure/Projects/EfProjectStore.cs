using AutoTestAi.Application.Projects;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoTestAi.Infrastructure.Projects;

/// <summary>EF Core implementation of the Projects persistence seam. No authorization here.</summary>
public sealed class EfProjectStore : IProjectStore
{
    private readonly AutoTestAiDbContext _db;

    public EfProjectStore(AutoTestAiDbContext db) => _db = db;

    public async Task<int> CountAccessibleAsync(
        string? externalIdentityId, bool isAdmin, string? search, CancellationToken ct)
        => await AccessibleProjects(externalIdentityId, isAdmin, search).CountAsync(ct);

    public async Task<IReadOnlyList<ProjectListRow>> ListAccessibleAsync(
        string? externalIdentityId, bool isAdmin, string? search, int skip, int take, CancellationToken ct)
    {
        var memberCounts = _db.ProjectMembers
            .GroupBy(m => m.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() });

        var rows = await AccessibleProjects(externalIdentityId, isAdmin, search)
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .GroupJoin(memberCounts,
                p => p.Id,
                mc => mc.ProjectId,
                (p, counts) => new { Project = p, Count = counts.Select(c => c.Count).FirstOrDefault() })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Select(r => new ProjectListRow(r.Project, r.Count)).ToList();
    }

    public Task<Project?> GetByIdAsync(Guid projectId, CancellationToken ct)
        => _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

    public Task<Project?> GetByKeyAsync(string normalizedKey, CancellationToken ct)
        => _db.Projects.FirstOrDefaultAsync(p => p.Key == normalizedKey, ct);

    public Task AddProjectAsync(Project project, CancellationToken ct)
        => _db.Projects.AddAsync(project, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct)
        => _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken ct)
        => _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), ct);

    public async Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct)
        => await _db.Roles.OrderBy(r => r.Name).AsNoTracking().ToListAsync(ct);

    public Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken ct)
        => _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);

    public async Task<Role> GetOrCreateRoleAsync(string name, string? description, CancellationToken ct)
    {
        var existing = await _db.Roles.FirstOrDefaultAsync(r => r.Name == name, ct);
        if (existing is not null) return existing;
        var role = new Role { Name = name, Description = description };
        await _db.Roles.AddAsync(role, ct);
        return role;
    }

    public Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
        => _db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);

    public async Task<IReadOnlyList<MemberRow>> ListMembersAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await (from member in _db.ProjectMembers
                          join user in _db.Users on member.UserId equals user.Id
                          join role in _db.Roles on member.RoleId equals role.Id
                          where member.ProjectId == projectId
                          orderby user.DisplayName
                          select new { member, user, role })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.Select(r => new MemberRow(
            r.user.Id, r.user.Email, r.user.DisplayName,
            r.role.Id, r.role.Name, r.member.CreatedAt)).ToList();
    }

    public Task<ProjectMember?> FindMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
        => _db.ProjectMembers.FirstOrDefaultAsync(
            m => m.ProjectId == projectId && m.UserId == userId, ct);

    public Task AddMemberAsync(ProjectMember member, CancellationToken ct)
        => _db.ProjectMembers.AddAsync(member, ct).AsTask();

    public Task RemoveMemberAsync(ProjectMember member, CancellationToken ct)
    {
        _db.ProjectMembers.Remove(member);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TestEnvironment>> ListEnvironmentsAsync(Guid projectId, CancellationToken ct)
        => await _db.Environments
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<TestEnvironment?> GetEnvironmentByIdAsync(Guid environmentId, CancellationToken ct)
        => _db.Environments.FirstOrDefaultAsync(e => e.Id == environmentId, ct);

    public Task AddEnvironmentAsync(TestEnvironment environment, CancellationToken ct)
        => _db.Environments.AddAsync(environment, ct).AsTask();

    public Task RecordAuditAsync(AuditEvent auditEvent, CancellationToken ct)
        => _db.AuditEvents.AddAsync(auditEvent, ct).AsTask();

    private IQueryable<Project> AccessibleProjects(string? externalIdentityId, bool isAdmin, string? search)
    {
        IQueryable<Project> query = _db.Projects;
        if (!isAdmin)
        {
            if (string.IsNullOrWhiteSpace(externalIdentityId)) return query.Where(p => false);
            var memberProjectIds = from member in _db.ProjectMembers
                                   join user in _db.Users on member.UserId equals user.Id
                                   where user.ExternalIdentityId == externalIdentityId && user.IsActive
                                   select member.ProjectId;
            query = query.Where(p => memberProjectIds.Contains(p.Id));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => p.Name.Contains(term) || p.Key.Contains(term));
        }
        return query;
    }
}
