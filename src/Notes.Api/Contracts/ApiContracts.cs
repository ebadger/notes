using System.Text.Json.Serialization;
using Notes.Api.Data;

namespace Notes.Api.Contracts;

public enum GrantAudience
{
    Team,
    Organization
}

public enum GrantPermission
{
    Read,
    Edit
}

public sealed record NoteContentRequest(string? Title, string? Body);

public sealed record GrantRequest(
    GrantAudience? Audience,
    Guid? TeamId,
    GrantPermission? Permission);

public sealed record SharingRequest(IReadOnlyList<GrantRequest?>? Grants);

public sealed record NoteResponse(
    Guid Id,
    Guid OwnerId,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version)
{
    public static NoteResponse From(Note note) =>
        new(note.Id, note.OwnerId, note.Title, note.Body, note.CreatedAt, note.UpdatedAt, note.Version);
}

public sealed record NoteSummary(
    Guid Id,
    Guid OwnerId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record GrantResponse(
    GrantAudience Audience,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TeamId,
    GrantPermission Permission);

public sealed record SharingResponse(IReadOnlyList<GrantResponse> Grants);

public sealed record TeamResponse(Guid Id, string Name);

public sealed record ListResponse<T>(IReadOnlyList<T> Items, long? NextOffset);
