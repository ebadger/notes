# Automated tests

The unit suite calls the service's actual validation, access-policy, API-key,
and version-tag helpers. It does not start the application or need PostgreSQL.

```powershell
dotnet test tests\Notes.Api.Tests\Notes.Api.Tests.csproj --filter Category=Unit
```

The integration suite runs the real application on a dynamically allocated
loopback Kestrel port and uses real PostgreSQL, including the committed
migrations and startup seeding. It does not use a substitute service, SQLite,
or an EF in-memory provider.

Set `NOTES_TEST_DB` locally to a PostgreSQL administration connection string.
The database login needs `CREATEDB` permission and access to the administration
database. Use a dedicated local/test server and do not commit its credentials.
For example, fill in your own values in this PowerShell command:

```powershell
$env:NOTES_TEST_DB = 'Host=localhost;Port=5432;Database=postgres;Username=<test-role>;Password=<test-password>'
dotnet test tests\Notes.Api.Tests\Notes.Api.Tests.csproj --filter Category=Integration
```

On Linux/WSL, export the same environment variable and use forward slashes in
the project path. `dotnet test` without a filter runs both categories.
If `NOTES_TEST_DB` is absent, unreachable, or cannot create databases, integration
tests **fail with an actionable error**; they are not silently skipped.

## Isolation and safety

Each integration suite creates one randomly named `notes_tests_<random>`
database. Only that explicitly created database is dropped at fixture disposal.
The supplied administration database is never migrated, reset, or dropped.
The application receives random test-only bearer keys through in-memory
configuration. Test credentials are not written to the repository.

Integration classes share a nonparallel collection. Most tests create their own
notes; pagination and seed-resurrection tests deliberately clear notes only
inside the fixture-owned database. Unit tests remain independent. Application
restart tests recreate the web host without resetting the database.

The race tests issue actual concurrent HTTP mutations. The revocation race also
holds an external PostgreSQL row lock, observes the owner's blocked CAS update,
then observes the editor's blocked CAS update before releasing the lock. This
proves that an edit authorized against the old grant cannot overwrite a completed
revocation; no arbitrary timing sleep stands in for the required ordering.

## Coverage

| Area | Checks |
| --- | --- |
| Authentication | All API routes require bearer keys; malformed/unknown credentials and identity-header/query-string bypasses fail; health is anonymous. |
| Authorization | Private direct/list nondisclosure, owner operations, team editors, organization readers, multiple-team strongest access and deduplication, no-membership organization access, revocation. |
| Content | Required string fields, trimming, exact UTF-16 title and decoded UTF-8 body boundaries, empty body, unknown fields, immutable ownership/grants, UTC timestamps. |
| Sharing | Defined enums, required/forbidden team IDs, unknown teams, duplicate targets, complete replacement, atomic failure, shared version increments. |
| Preconditions | Missing `428`, stale `412`, invalid `400`, strong canonical tags, unsupported weak/wildcard/list/zero/leading-zero/overflow forms, authorization before version disclosure. |
| HTTP | Problem Details and `no-store`, malformed route IDs, exact 1 MiB request boundaries using both Content-Length and streamed HTTP/1.1 chunked bodies. |
| Pagination | Defaults and validation, stable timestamp/UUID ordering, full pages after access filtering, end offsets, no duplicate notes. |
| Concurrency | Competing content saves, competing sharing replacements, and deterministically ordered editor/revocation writes. |
| Database | Check constraints, foreign keys, partial unique indexes, membership/key uniqueness, cascading grants, migrations, idempotent seeding, restart persistence, no note resurrection, API-key hash rotation. |

Application restart tests do not restart a shared PostgreSQL server. A real
database-process/container stop/start and deployment persistence check belongs
to the runtime quickstart validation, not a test fixture that may be given
access to a shared server.
