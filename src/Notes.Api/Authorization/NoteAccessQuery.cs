using Microsoft.EntityFrameworkCore;
using Notes.Api.Contracts;
using Notes.Api.Data;

namespace Notes.Api.Authorization;

public sealed class AccessibleNote
{
    public required Note Note { get; init; }
    public AccessLevel Access { get; init; }
}

public static class NoteAccessQuery
{
    public static IQueryable<AccessibleNote> ForUser(NotesDbContext database, Guid userId) =>
        database.Notes.AsNoTracking()
            .Select(note => new AccessibleNote
            {
                Note = note,
                Access = note.OwnerId == userId
                    ? AccessLevel.Owner
                    : note.Grants.Any(grant =>
                        grant.Permission == GrantPermission.Edit &&
                        (grant.Audience == GrantAudience.Organization ||
                         grant.Team!.Memberships.Any(membership => membership.UserId == userId)))
                        ? AccessLevel.Edit
                        : note.Grants.Any(grant =>
                            grant.Audience == GrantAudience.Organization ||
                            grant.Team!.Memberships.Any(membership => membership.UserId == userId))
                            ? AccessLevel.Read
                            : AccessLevel.None
            })
            .Where(result => result.Access != AccessLevel.None);
}
