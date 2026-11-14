using Notes.Api.Contracts;

namespace Notes.Api.Data;

public sealed class User
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public ICollection<TeamMembership> Memberships { get; set; } = [];
}

public sealed class Team
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<TeamMembership> Memberships { get; set; } = [];
}

public sealed class TeamMembership
{
    public Guid UserId { get; set; }
    public Guid TeamId { get; set; }
    public User User { get; set; } = null!;
    public Team Team { get; set; } = null!;
}

public sealed class Note
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
    public ICollection<NoteGrant> Grants { get; set; } = [];
}

public sealed class NoteGrant
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;
    public GrantAudience Audience { get; set; }
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
    public GrantPermission Permission { get; set; }
}

public sealed class SeedState
{
    public int Id { get; set; }
    public DateTimeOffset InitializedAt { get; set; }
}
