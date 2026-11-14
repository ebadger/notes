using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Authorization;
using Notes.Api.Contracts;
using Notes.Api.Data;
using Notes.Api.Http;
using Notes.Api.Validation;

namespace Notes.Api.Endpoints;

public static class NotesEndpoints
{
    public static void MapNotesApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization();
        api.MapPost("/notes", CreateAsync);
        api.MapGet("/notes", ListAsync);
        api.MapGet("/notes/{id}", GetAsync);
        api.MapPut("/notes/{id}", UpdateAsync);
        api.MapDelete("/notes/{id}", DeleteAsync);
        api.MapGet("/notes/{id}/sharing", GetSharingAsync);
        api.MapPut("/notes/{id}/sharing", ReplaceSharingAsync);
        api.MapGet("/teams", ListTeamsAsync);
        api.MapFallback("/{**path}", (HttpContext context) =>
            ApiProblems.Result(context, StatusCodes.Status404NotFound, "The requested API route was not found."));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        var request = await ApiRequest.ReadJsonAsync<NoteContentRequest>(context);
        var errors = InputValidation.ValidateContent(request);
        if (errors.Count != 0)
        {
            return ApiProblems.Validation(context, errors);
        }

        var now = UtcTimestamp.Now();
        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = UserId(context),
            Title = request.Title!.Trim(),
            Body = request.Body!,
            CreatedAt = now,
            UpdatedAt = now
        };
        database.Notes.Add(note);
        await database.SaveChangesAsync(cancellationToken);
        context.Response.Headers.ETag = VersionTags.Format(note.Version);
        return TypedResults.Created($"/api/notes/{note.Id:D}", NoteResponse.From(note));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        if (!ReadIntegerQuery(context, "limit", 20, out var limit) || limit is < 1 or > 100 ||
            !ReadIntegerQuery(context, "offset", 0, out var offset) || offset < 0)
        {
            return ApiProblems.Result(context, StatusCodes.Status400BadRequest,
                "Limit must be an integer from 1 through 100; offset must be a nonnegative integer.");
        }

        var items = await NoteAccessQuery.ForUser(database, UserId(context))
            .OrderByDescending(result => result.Note.CreatedAt)
            .ThenByDescending(result => result.Note.Id)
            .Skip(offset)
            .Take(limit + 1)
            .Select(result => new NoteSummary(
                result.Note.Id,
                result.Note.OwnerId,
                result.Note.Title,
                result.Note.CreatedAt,
                result.Note.UpdatedAt,
                result.Note.Version))
            .ToListAsync(cancellationToken);
        long? nextOffset = items.Count > limit ? (long)offset + limit : null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
        }

        return TypedResults.Ok(new ListResponse<NoteSummary>(items, nextOffset));
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await FindAsync(
            id, NoteOperation.Read, context, database, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var note = snapshot!.Note;
        context.Response.Headers.ETag = VersionTags.Format(note.Version);
        return TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<IResult> UpdateAsync(
        string id, HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await FindAsync(
            id, NoteOperation.EditContent, context, database, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var note = snapshot!.Note;
        if (CheckPrecondition(context, note.Version, out var version) is { } preconditionError)
        {
            return preconditionError;
        }

        var request = await ApiRequest.ReadJsonAsync<NoteContentRequest>(context);
        var errors = InputValidation.ValidateContent(request);
        if (errors.Count != 0)
        {
            return ApiProblems.Validation(context, errors);
        }

        var title = request.Title!.Trim();
        var updatedAt = UtcTimestamp.Now();
        var count = await database.Notes
            .Where(candidate => candidate.Id == note.Id && candidate.Version == version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Title, title)
                .SetProperty(candidate => candidate.Body, request.Body!)
                .SetProperty(candidate => candidate.UpdatedAt, updatedAt)
                .SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1),
                cancellationToken);
        if (count == 0)
        {
            return StaleVersion(context);
        }

        note.Title = title;
        note.Body = request.Body!;
        note.UpdatedAt = updatedAt;
        note.Version = version + 1;
        context.Response.Headers.ETag = VersionTags.Format(note.Version);
        return TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await FindAsync(
            id, NoteOperation.Delete, context, database, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var note = snapshot!.Note;
        if (CheckPrecondition(context, note.Version, out var version) is { } preconditionError)
        {
            return preconditionError;
        }

        var count = await database.Notes
            .Where(candidate => candidate.Id == note.Id && candidate.Version == version)
            .ExecuteDeleteAsync(cancellationToken);
        return count == 0 ? StaleVersion(context) : TypedResults.NoContent();
    }

    private static async Task<IResult> GetSharingAsync(
        string id, HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var noteId))
        {
            return InvalidId(context);
        }

        var snapshot = await NoteAccessQuery.ForUser(database, UserId(context))
            .Where(result => result.Note.Id == noteId)
            .AsSingleQuery()
            .Select(result => new
            {
                result.Access,
                result.Note.Version,
                Grants = result.Note.Grants
                    .Select(grant => new GrantResponse(grant.Audience, grant.TeamId, grant.Permission))
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null)
        {
            return NotFound(context);
        }

        if (!AccessPolicy.Can(snapshot.Access, NoteOperation.ManageSharing))
        {
            return Forbidden(context);
        }

        context.Response.Headers.ETag = VersionTags.Format(snapshot.Version);
        return TypedResults.Ok(new SharingResponse(OrderGrants(snapshot.Grants)));
    }

    private static async Task<IResult> ReplaceSharingAsync(
        string id, HttpContext context, NotesDbContext database, CancellationToken cancellationToken)
    {
        var (snapshot, error) = await FindAsync(
            id, NoteOperation.ManageSharing, context, database, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var note = snapshot!.Note;
        if (CheckPrecondition(context, note.Version, out var version) is { } preconditionError)
        {
            return preconditionError;
        }

        var request = await ApiRequest.ReadJsonAsync<SharingRequest>(context);
        var teamIds = await database.Teams.Select(team => team.Id).ToHashSetAsync(cancellationToken);
        var errors = InputValidation.ValidateSharing(request, teamIds);
        if (errors.Count != 0)
        {
            return ApiProblems.Validation(context, errors);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var updatedAt = UtcTimestamp.Now();
        var count = await database.Notes
            .Where(candidate => candidate.Id == note.Id && candidate.Version == version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.UpdatedAt, updatedAt)
                .SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1),
                cancellationToken);
        if (count == 0)
        {
            return StaleVersion(context);
        }

        // Delete immediately, before tracking replacements that may have the same unique targets.
        await database.NoteGrants.Where(grant => grant.NoteId == note.Id)
            .ExecuteDeleteAsync(cancellationToken);
        var grants = request.Grants!.Select(grant => new NoteGrant
        {
            Id = Guid.NewGuid(),
            NoteId = note.Id,
            Audience = grant!.Audience!.Value,
            TeamId = grant.TeamId,
            Permission = grant.Permission!.Value
        }).ToArray();
        database.NoteGrants.AddRange(grants);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        context.Response.Headers.ETag = VersionTags.Format(version + 1);
        return TypedResults.Ok(new SharingResponse(OrderGrants(
            grants.Select(grant => new GrantResponse(grant.Audience, grant.TeamId, grant.Permission)))));
    }

    private static async Task<IResult> ListTeamsAsync(
        NotesDbContext database, CancellationToken cancellationToken)
    {
        var teams = await database.Teams.AsNoTracking()
            .OrderBy(team => team.Name).ThenBy(team => team.Id)
            .Select(team => new TeamResponse(team.Id, team.Name))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new ListResponse<TeamResponse>(teams, null));
    }

    private static async Task<(AccessibleNote? Snapshot, IResult? Error)> FindAsync(
        string id,
        NoteOperation operation,
        HttpContext context,
        NotesDbContext database,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var noteId))
        {
            return (null, InvalidId(context));
        }

        var snapshot = await NoteAccessQuery.ForUser(database, UserId(context))
            .SingleOrDefaultAsync(result => result.Note.Id == noteId, cancellationToken);
        if (snapshot is null)
        {
            return (null, NotFound(context));
        }

        return AccessPolicy.Can(snapshot.Access, operation)
            ? (snapshot, null)
            : (null, Forbidden(context));
    }

    private static IResult? CheckPrecondition(HttpContext context, long currentVersion, out long version)
    {
        version = 0;
        if (!context.Request.Headers.TryGetValue("If-Match", out var values))
        {
            return ApiProblems.Result(context, StatusCodes.Status428PreconditionRequired,
                "If-Match is required. Send the strong ETag returned by the latest read.");
        }

        if (values.Count != 1 || !VersionTags.TryParse(values[0], out version))
        {
            return ApiProblems.Result(context, StatusCodes.Status400BadRequest,
                "If-Match must contain exactly one strong, positive numeric version tag.");
        }

        return version == currentVersion ? null : StaleVersion(context);
    }

    private static bool ReadIntegerQuery(HttpContext context, string name, int defaultValue, out int value)
    {
        value = defaultValue;
        return !context.Request.Query.TryGetValue(name, out var values) ||
               (values.Count == 1 &&
                int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value));
    }

    private static Guid UserId(HttpContext context) =>
        Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IReadOnlyList<GrantResponse> OrderGrants(IEnumerable<GrantResponse> grants) =>
        grants.OrderBy(grant => grant.Audience).ThenBy(grant => grant.TeamId).ToArray();

    private static IResult InvalidId(HttpContext context) =>
        ApiProblems.Result(context, StatusCodes.Status400BadRequest, "The note ID must be a valid UUID.");

    private static IResult NotFound(HttpContext context) =>
        ApiProblems.Result(context, StatusCodes.Status404NotFound, "The note was not found.");

    private static IResult Forbidden(HttpContext context) =>
        ApiProblems.Result(context, StatusCodes.Status403Forbidden,
            "You can read this note but do not have permission to perform this action.");

    private static IResult StaleVersion(HttpContext context) =>
        ApiProblems.Result(context, StatusCodes.Status412PreconditionFailed,
            "The note has changed. Fetch the latest representation and reconcile before retrying.");
}
