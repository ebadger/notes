using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Authentication;
using Notes.Api.Contracts;

namespace Notes.Api.Data;

public static class DemoIds
{
    public static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Carol = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid Dana = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid TeamA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TeamB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
}

public static class DatabaseInitializer
{
    private static readonly (Guid Id, string Name)[] Users =
    [
        (DemoIds.Alice, "Alice"),
        (DemoIds.Bob, "Bob"),
        (DemoIds.Carol, "Carol"),
        (DemoIds.Dana, "Dana")
    ];

    private static readonly (Guid Id, string Name)[] Teams =
    [
        (DemoIds.TeamA, "Team A"),
        (DemoIds.TeamB, "Team B")
    ];

    private static readonly (Guid UserId, Guid TeamId)[] Memberships =
    [
        (DemoIds.Alice, DemoIds.TeamA),
        (DemoIds.Bob, DemoIds.TeamA),
        (DemoIds.Carol, DemoIds.TeamB),
        (DemoIds.Dana, DemoIds.TeamA),
        (DemoIds.Dana, DemoIds.TeamB)
    ];

    public static async Task InitializeAsync(
        NotesDbContext database,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var configuredKeys = ReadConfiguredKeys(configuration);
        var desiredHashes = Users.ToDictionary(user => user.Id, user => ApiKeys.Hash(configuredKeys[user.Name]));
        if (configuration.GetValue("Database:ApplyMigrations", true))
        {
            await database.Database.MigrateAsync(cancellationToken);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var userIds = Users.Select(user => user.Id).ToArray();
        var existingUsers = await database.Users.Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        // Clear changed hashes inside the transaction so valid key swaps cannot violate uniqueness.
        foreach (var user in existingUsers.Values)
        {
            if (user.ApiKeyHash != desiredHashes[user.Id])
            {
                user.ApiKeyHash = Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        foreach (var (id, name) in Users)
        {
            if (!existingUsers.TryGetValue(id, out var user))
            {
                user = new User { Id = id };
                database.Users.Add(user);
            }

            user.DisplayName = name;
            user.ApiKeyHash = desiredHashes[id];
        }

        var teamIds = Teams.Select(team => team.Id).ToArray();
        var existingTeams = await database.Teams.Where(team => teamIds.Contains(team.Id))
            .ToDictionaryAsync(team => team.Id, cancellationToken);
        foreach (var (id, name) in Teams)
        {
            if (!existingTeams.TryGetValue(id, out var team))
            {
                team = new Team { Id = id };
                database.Teams.Add(team);
            }

            team.Name = name;
        }

        var existingMemberships = await database.TeamMemberships
            .Where(membership => userIds.Contains(membership.UserId))
            .Select(membership => new { membership.UserId, membership.TeamId })
            .ToListAsync(cancellationToken);
        foreach (var (userId, teamId) in Memberships)
        {
            if (!existingMemberships.Any(membership =>
                    membership.UserId == userId && membership.TeamId == teamId))
            {
                database.TeamMemberships.Add(new TeamMembership { UserId = userId, TeamId = teamId });
            }
        }

        if (!await database.SeedStates.AnyAsync(state => state.Id == 1, cancellationToken))
        {
            AddInitialNotes(database);
            database.SeedStates.Add(new SeedState { Id = 1, InitializedAt = UtcTimestamp.Now() });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static IReadOnlyDictionary<string, string> ReadConfiguredKeys(IConfiguration configuration)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var uniqueHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, name) in Users)
        {
            var key = configuration[$"Demo:ApiKeys:{name}"];
            if (!ApiKeys.HasValidFormat(key))
            {
                throw new InvalidOperationException(
                    $"Demo:ApiKeys:{name} is required and must contain at least 32 characters without whitespace.");
            }

            var hash = ApiKeys.Hash(key!);
            if (!uniqueHashes.Add(hash))
            {
                throw new InvalidOperationException("Demo API keys must be unique for each user.");
            }

            keys.Add(name, key!);
        }

        return keys;
    }

    private static void AddInitialNotes(NotesDbContext database)
    {
        var now = UtcTimestamp.Now();
        var privateNote = new Note
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            OwnerId = DemoIds.Alice,
            Title = "Alice's private notebook",
            Body = "# Private notes\nOnly Alice can read or change this note.",
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now.AddMinutes(-3)
        };
        var teamNote = new Note
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000102"),
            OwnerId = DemoIds.Alice,
            Title = "Team A working notes",
            Body = "# Team collaboration\nTeam A can edit this note. Alice controls sharing and deletion.",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-2),
            Grants =
            [
                new NoteGrant
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000201"),
                    Audience = GrantAudience.Team,
                    TeamId = DemoIds.TeamA,
                    Permission = GrantPermission.Edit
                }
            ]
        };
        var organizationNote = new Note
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000103"),
            OwnerId = DemoIds.Alice,
            Title = "Release checklist",
            Body = "## Before release\n- Review outstanding issues\n\nTeam A can edit; the organization can read.",
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now.AddMinutes(-1),
            Grants =
            [
                new NoteGrant
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000202"),
                    Audience = GrantAudience.Team,
                    TeamId = DemoIds.TeamA,
                    Permission = GrantPermission.Edit
                },
                new NoteGrant
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000203"),
                    Audience = GrantAudience.Organization,
                    Permission = GrantPermission.Read
                }
            ]
        };
        database.Notes.AddRange(privateNote, teamNote, organizationNote);
    }
}
