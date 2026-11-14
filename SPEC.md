# Notes service specification

**Status:** Draft based on our design conversation. This document does not start implementation.

**Delivery target:** A runnable REST backend and README, within a one-hour implementation timebox. Optimize for clarity, correctness, and defensible tradeoffs, not production completeness.

## 1. Product goal

Make the same note useful to an individual, a team, and the organization without copying it or changing its fundamental type.

A person can keep a note private, collaborate with selected teams, and publish it to the organization. Ownership stays with the individual; audience and permission are separate concerns.

The defining example is: **Alice owns a note, her team can edit it, and the rest of the organization can read it.**

## 2. Decisions from the conversation

| Area | Decision |
| --- | --- |
| Language | C#. |
| Database | PostgreSQL, accepting the extra setup in exchange for a server database from the start. |
| Hosting | The existing WSL instance on this machine. No cloud deployment in this version. |
| Organization boundary | One organization containing multiple teams. No multi-tenant hosting in this version. |
| Ownership | Every note has one individual owner. Teams and the organization receive access, not ownership. |
| Sharing | A note can have multiple audience-specific grants with different permissions. |
| Permissions | Each grant allows read or edit; edit includes read. |
| Identity | Seeded users and teams, with per-user API keys. No registration or login workflow. |
| Concurrent editing | Reject stale saves. Clients fetch the latest version and reconcile; never silently overwrite a newer edit. |
| Design principle | Favor a flexible permission model and a sensible growth path while keeping the initial implementation small. |

The remaining details below are implementation defaults proposed to make those decisions concrete.

## 3. Technology and application shape

- **.NET 10 LTS and ASP.NET Core Minimal APIs:** a small, typed HTTP service on a supported release.
- **EF Core 10 with the compatible Npgsql provider:** relational mapping, migrations, and database-enforced optimistic concurrency.
- **PostgreSQL 17:** a supported, pinned major version for the local environment.
- **Docker Compose in WSL:** application and database, with a persistent database volume and a documented local quickstart.
- **xUnit:** focused integration coverage against an isolated PostgreSQL test database.

Use one application project and one test project. Keep HTTP contracts, access rules, and persistence responsibilities clear without adding a generic repository framework, CQRS framework, event bus, or separate permissions service.

The application holds no mutable note or permission state in memory between requests. PostgreSQL is authoritative. Authentication produces a user identity; note authorization does not depend on the mechanics of API-key authentication.

The choice of .NET 10 rather than a newly released major favors the LTS baseline. Use current compatible servicing patches, not unpinned preview packages. See the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## 4. In-scope behavior

### Notes

A note has a title and a Markdown body. The backend stores the body as text; it does not render or interpret Markdown.

Users can create private notes, list notes they can access, read a note, and update its title and body when permitted. Only the owner can delete a note.

Creation assigns the authenticated user as owner. Ownership is immutable in this version. Content endpoints cannot change ownership or sharing.

### Sharing

The owner manages a collection of grants. Each grant targets either one existing team or the organization and allows `read` or `edit`.

Rules:

- No grants means private to the owner.
- The owner always retains full control, regardless of grants or team membership.
- Users may belong to multiple teams. Effective access is the strongest matching grant.
- An organization grant covers every authenticated user in this organization, including users without team membership. It never means anonymous/public access.
- Owners may share their own notes with any existing team in the organization.
- Editors can change content but cannot delete the note, transfer ownership, or change sharing.
- There is at most one grant per team per note and one organization grant per note.
- No explicit deny rules, grant expiration, per-user sharing, or delegation.

The owner replaces the entire sharing collection atomically. An empty collection makes the note private again. New requests use the updated permissions; already returned content cannot be recalled.

### Team discovery

Authenticated users can list the existing teams to discover valid sharing targets. Creating teams, changing memberships, and managing users are outside the public API.

## 5. Access matrix

| Effective access | Read note | Edit content | Read/change sharing | Delete |
| --- | --- | --- | --- | --- |
| Owner | Yes | Yes | Yes | Yes |
| Edit grant | Yes | Yes | No | No |
| Read grant | Yes | No | No | No |
| No matching grant | No | No | No | No |

Evaluate access using one shared policy/query implementation rather than independent rules in each endpoint.

List filtering must happen in PostgreSQL, before pagination. Do not load all notes and filter them in application memory. Multiple matching grants must not duplicate a note in the list.

## 6. Relational model

For the implemented SQL schema, including column names and maintenance tables,
see the [database schema diagrams](DATABASE_SCHEMA.md).

Use UUID identifiers and UTC timestamps.

| Entity | Essential fields and constraints |
| --- | --- |
| User | `id`, `displayName`, unique `apiKeyHash`; one active API key per seeded user. |
| Team | `id`, `name`. |
| TeamMembership | `userId`, `teamId`; composite primary key and foreign keys. |
| Note | `id`, `ownerId`, `title`, `body`, `createdAt`, `updatedAt`, positive `version`; owner foreign key. |
| NoteGrant | `id`, `noteId`, `audience`, nullable `teamId`, `permission`; note/team foreign keys. |

`audience` is `team` or `organization`. A team grant must have a team ID; an organization grant must not. Enforce this and allowed permission values with database constraints, not only request validation.

Enforce grant uniqueness with appropriate PostgreSQL unique indexes, including the organization case where `teamId` is null. Deleting a note cascades to its grants.

Index note ownership, membership lookup, grant lookup, and the note listing order. Keep the indexes aligned with the actual access query rather than adding speculative indexes.

The organization is the deployment boundary, not a tenant identifier. Supporting multiple organizations later requires an explicit tenancy migration and isolation review; it is not merely another grant type.

## 7. HTTP contract

Base path: `/api`. All routes below require `Authorization: Bearer <opaque-api-key>`.

| Method and path | Behavior | Success |
| --- | --- | --- |
| `POST /api/notes` | Create a private note owned by the caller. | `201`, note, `Location`, and `ETag`. |
| `GET /api/notes?limit=20&offset=0` | List only accessible note summaries. | `200`, paginated collection. |
| `GET /api/notes/{id}` | Read an accessible note. | `200`, note and `ETag`. |
| `PUT /api/notes/{id}` | Replace title and body; preserve ownership and sharing. | `200`, updated note and `ETag`. |
| `DELETE /api/notes/{id}` | Owner deletes the note and its grants. | `204`. |
| `GET /api/notes/{id}/sharing` | Owner reads the current sharing collection. | `200`, collection and `ETag`. |
| `PUT /api/notes/{id}/sharing` | Owner atomically replaces the sharing collection. | `200`, collection and new `ETag`. |
| `GET /api/teams` | List available team IDs and names. | `200`. |

Content creation/update request:

```json
{
  "title": "Release checklist",
  "body": "## Before release\n- Review outstanding issues"
}
```

Note response:

```json
{
  "id": "<uuid>",
  "ownerId": "<uuid>",
  "title": "Release checklist",
  "body": "## Before release\n- Review outstanding issues",
  "createdAt": "<UTC timestamp>",
  "updatedAt": "<UTC timestamp>",
  "version": 1
}
```

Sharing request/response:

```json
{
  "grants": [
    { "audience": "team", "teamId": "<uuid>", "permission": "edit" },
    { "audience": "organization", "permission": "read" }
  ]
}
```

Lists return `{ "items": [...], "nextOffset": null }`, with a numeric `nextOffset` when more accessible notes exist. Summaries omit the body. Sort by `createdAt` descending, then `id` descending for deterministic ties.

`limit` defaults to 20 and accepts 1 through 100. `offset` defaults to 0 and must be nonnegative. Offset pagination is an explicit timebox tradeoff: simple to implement and explain, but deep pages are less efficient and concurrent insertions can shift page boundaries.

## 8. Conflict handling and consistency

Use a monotonically increasing note version, beginning at 1. The version covers both content and sharing.

Every successful content or sharing update increments the version once and updates `updatedAt`. `createdAt` does not change.

Detail and sharing reads return a strong `ETag`, for example `"3"`. Content updates, sharing updates, and deletion require the caller to echo the current value in `If-Match`.

- Missing `If-Match`: `428 Precondition Required`.
- A stale version: `412 Precondition Failed`; no mutation occurs.
- Accept exactly one strong version tag generated by this API. Malformed values, weak tags, wildcard tags, and tag lists are unsupported and return `400`.

The database must perform the version comparison as part of the update/delete, not only as an earlier application check. Sharing replacement and its version increment are one transaction.

Authorization and versioning must remain consistent during a sharing change. If an editor's permission is revoked after an edit was authorized but before it commits, the sharing version increment must prevent that edit from committing with the old version. Avoid reading permission state and its associated version from inconsistent snapshots.

Two saves using the same version cannot both succeed. A client receiving `412` fetches the latest note and reconciles or asks its user to retry. The server does not merge content automatically.

## 9. Validation and errors

Use request-specific DTOs; do not bind persistence entities directly.

- Title is required, trimmed, and must contain 1 through 200 UTF-16 code units after trimming.
- Body is a required string, may be empty, and is limited to 64 KiB of decoded UTF-8 text.
- Limit request bodies to 1 MiB.
- Reject unknown request fields, invalid enums, malformed IDs, duplicate grant targets, unknown teams, and invalid audience/team combinations.
- Invalid sharing replacement changes nothing; it cannot partially apply.

Return standard ASP.NET Core Problem Details for errors:

| Status | Meaning |
| --- | --- |
| `400` | Invalid input or unsupported precondition syntax. |
| `401` | Missing or invalid API key. |
| `403` | Caller can read the note but lacks permission for the requested action. |
| `404` | Note does not exist, or caller has no access to it. Do not distinguish these cases. |
| `412` | Version precondition failed. |
| `413` | Request exceeds the overall request-size limit. |
| `428` | Required version precondition is missing. |
| `500` | Unexpected server failure; sanitized response, server-side diagnostic logging. |

Authenticate and establish access before returning resource-specific version information. Do not leak titles, sharing information, or versions to callers without access.

Authenticated responses use `Cache-Control: no-store`. Log useful request/error identifiers without API keys or note bodies.

## 10. Authentication, seeding, and running

API keys are high-entropy opaque bearer tokens, not user IDs. Store only their hashes in PostgreSQL. Do not hardcode real credentials, accept identity headers as proof of authentication, or print credentials in normal logs.

Provide documented local setup for database credentials and demo API keys through environment configuration. Reject missing or invalid required configuration clearly.

Seed a small reproducible organization:

- Alice owns example notes and belongs to Team A.
- Bob belongs to Team A.
- Carol belongs to Team B.
- Dana belongs to both teams.

These are fictional demonstration identities. Seed users, teams, and memberships idempotently, without deleting or resetting notes and grants on restart. Document how configured demo keys are reconciled with their stored hashes.

Run the application and database in the existing WSL distribution. Keep application files and database storage on its Linux filesystem rather than a Windows-mounted directory. Confirm the available container runtime during implementation rather than assuming it is installed.

After required local configuration, `docker compose up --build` starts the service and PostgreSQL from WSL. Apply committed migrations in the documented single-instance demo startup path. A normal stop/start preserves notes through the database volume. Document the URL used to reach the service from the Windows host.

Bind the demo to localhost. Plain HTTP is acceptable only for this local demonstration; any remote deployment requires TLS. For multiple application replicas, move migration execution to a separate deployment step and supply consistent identity configuration.

## 11. Definition of done

The submission contains the service, committed schema migrations, a Docker-based quickstart, focused automated integration coverage, and a README.

Demonstrate the following against real PostgreSQL rather than EF Core's in-memory provider:

| Scenario | Expected result |
| --- | --- |
| Alice creates a note | It is private; Alice can retrieve it and other users cannot. |
| Team A receives edit access | Bob can read/edit but cannot delete or change sharing. |
| Organization receives read access | Carol can read but not edit; Bob still has edit access. |
| User matches several grants | Strongest permission wins and listing returns the note once. |
| Owner replaces grants with an empty collection | Subsequent requests by other users cannot read the note. |
| Invalid grant replacement | Existing grants and version remain unchanged. |
| Two updates use the same version | Exactly one succeeds; the other receives `412`. |
| Content edit races with revocation | An edit based on the pre-revocation version cannot overwrite the revocation. |
| Unauthorized caller fetches a note directly or lists notes | Neither path exposes an inaccessible note. |
| Service/database restart without deleting the volume | Notes and sharing survive. |

Also cover missing/invalid credentials, request boundaries, owner-only operations, missing version preconditions, and deterministic pagination.

The README explains prerequisites, configuration, startup, example requests for the seeded users, the test command, the permission model, conflict recovery, and the tradeoffs below. It must distinguish demonstrated behavior from future intentions.

## 12. Explicit non-goals and tradeoffs

No frontend, Markdown rendering, rich-text editor, attachments, tags, full-text search, real-time co-editing, automatic merge, note history, soft deletion, ownership transfer, user-to-user sharing, account lifecycle, team administration, multiple tenants, or production identity-provider integration.

The flexible grant model is the main investment in extensibility. A relational schema, database-side access filtering, bounded lists, and stateless request handling give a reasonable growth path. They are not a claim of measured throughput or production readiness.

PostgreSQL adds setup but provides shared durable state and transactional concurrency. API keys keep authentication demonstrable while deliberately omitting identity lifecycle. One organization avoids tenant-isolation scope. Offset pagination avoids extra cursor machinery. Owner-only sharing avoids a second administrative role system.

The one-hour budget is tight. Preserve the agreed access rules, conflict protection, runnable quickstart, and honest README before adding convenience features. Do not label incomplete behavior as working.
