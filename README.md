# Notes service

A .NET 10 / ASP.NET Core REST service backed by PostgreSQL 17, implementing
[SPEC.md](SPEC.md). Notes retain an individual owner while teams and the
organization receive independent read or edit grants. A static manual test
workbench is included; it is not a product frontend or Markdown renderer.

See the [database schema diagrams](DATABASE_SCHEMA.md) for the implemented
PostgreSQL tables, keys, relationships, constraints, and indexes.

## Run in WSL

Keep this checkout on the distribution's **Linux filesystem**, such as
`~/notes-service`, not under `/mnt/c`. Docker's data directory and the database
volume must also be on the Linux filesystem.

Prerequisites: a working WSL 2 distribution, Docker Engine with Compose V2, and
OpenSSL. The containers include the .NET SDK/runtime; a host SDK is unnecessary.
On Ubuntu with these packages available:

```bash
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-v2 openssl
sudo systemctl enable --now docker
```

The commands below assume permission to use Docker. Prefix Docker commands with
`sudo` if your account does not have access to its Unix socket. Do not expose an
unauthenticated Docker TCP socket.

From the checkout:

```bash
bash scripts/configure-demo.sh
docker compose up --build -d
curl --fail http://localhost:8080/health
```

The setup script generates five independent cryptographically random values in
a private, Git-ignored `.env`: one database password and four demo bearer keys.
It refuses to overwrite an existing file. Alternatively, copy `.env.example` to
`.env` and fill in every required value. Do not commit this file, share its
contents in logs, or bake it into an image; the Docker build excludes it.

Open **http://localhost:8080/** from the Windows host running WSL. The API is at
`http://localhost:8080/api`. Compose publishes the service only on loopback; the
demo database has no published port. `NOTES_PORT` in `.env` changes the HTTP port.
Startup applies the committed EF migrations and seeds the demo organization.
Check startup failures with `docker compose logs --tail 100 api`.

### WSL on another machine

Run the same stack inside that machine's WSL distribution. With an SSH endpoint
that lands in that distribution, forward its loopback listener:

```powershell
ssh -N -L 127.0.0.1:8080:127.0.0.1:8080 <user>@<machine>
```

Keep the tunnel open and visit **http://localhost:8080/** on your client machine.
The tunnel encrypts traffic; do not change Compose to publish plaintext HTTP to
the LAN as a substitute. If local port 8080 is occupied, use 8081 on the left.
Never disable SSH host-key checking.

### Stop, restart, and credentials

```bash
docker compose stop
docker compose start
# Rebuild after a source or image change:
docker compose up --build -d
```

The `notes-data` named volume preserves notes and sharing across application and
database container restarts and `docker compose down`. **Do not use
`docker compose down --volumes`** unless you intentionally want to erase the
demo database.

Keep the database password in `.env` consistent with the existing volume.
Changing `POSTGRES_PASSWORD` does not change a PostgreSQL role's password in an
already initialized volume; rotate the database role and application
configuration together.

To rotate a demo key, replace that user's `DEMO_*_KEY` in `.env` with a new random
value and recreate the API with `docker compose up -d api`. Startup reconciles
the stored SHA-256 hashes transactionally, including valid key swaps. Old keys
stop authenticating after reconciliation. Keys must be distinct, at least 32
characters, and contain no whitespace. Only their hashes are stored in
PostgreSQL. Restarting never resets notes or grants, and a database seed marker
prevents deleted example notes from being recreated.

## Manual validation

Open `/` on the running service. The page sends requests to that service's
`/api` and therefore reads and writes its real PostgreSQL database; it is not
a mock, a separate browser database, or a Markdown renderer.

For one-click demo users, explicitly enable **local demo browser access**:

```bash
DEMO_BROWSER_KEYS=true docker compose up --build -d api
```

This opt-in is **off by default**. It maps to `Demo:EnableBrowserKeys` (or
`Demo__EnableBrowserKeys` for direct execution); use `DEMO_BROWSER_KEYS=false`
with the same Compose command to disable it again. You can instead set the
flag in your private `.env` when you deliberately want it to survive later
Compose invocations.

**Anyone who can open the local demo page can use all four identities while
this mode is enabled.** Do not enable it on a shared or publicly reachable
deployment or for data that should remain private from other demo users.
Keep the Compose loopback binding or the documented SSH tunnel. The request
header checks below defend browser access; they are not authentication and do
not replace that network boundary.

With this mode enabled, the page loads configured keys at runtime and is ready
to use: select **Alice**, **Bob**, **Carol**, or **Dana**. No file picker or key
pasting is needed. Keys are not compiled into the HTML/JavaScript, committed to
Git, stored in browser storage, or shown in the response inspector. Only the
four demo keys are returned; database credentials are never exposed.

When automatic access is disabled, expand **Manual key and authentication
checks** and paste a key from the private `.env` generated during setup:

| Demo user | Key assignment in `.env` | Membership |
| --- | --- | --- |
| Alice | `DEMO_ALICE_KEY` | Team A; owns the initial example notes |
| Bob | `DEMO_BOB_KEY` | Team A |
| Carol | `DEMO_CAROL_KEY` | Team B |
| Dana | `DEMO_DANA_KEY` | Team A and Team B |

Selecting a demo user refreshes that user's accessible notes and teams. Manual
keys and **Forget keys** remain available to test invalid or missing credentials.
Keys stay only in the tab's memory, never in URLs or browser storage; a reload
starts fresh and fetches them again only when local demo access is enabled.
Switching identity or key clears previously fetched content and pending
requests. Identity labels are conveniences, not authentication or authorization.

The normal view shows notes and one editor. Creation, sharing, the second
snapshot, precondition overrides, and raw response JSON are available in focused
expandable sections instead of occupying the whole page.

Create a private note as Alice; creation opens it in the main editor. Choose
**Sharing**, select **Team A edits + organization reads**, then **Save sharing**.
Bob can read and edit, but cannot delete or
manage sharing. Carol can read but cannot edit. Dana gets the strongest matching
grant and sees the note only once in a list. An empty sharing collection makes
the note private again.

The two editors keep independent snapshots. Load a note into A, clone its
snapshot into B, save A, then save B: B must receive `412`, and its unsaved text
remains available to reconcile. The sharing editor has its own ETag. Each editor
can use a fetched tag, a manually entered tag, or omit `If-Match`, making stale,
malformed, and missing preconditions easy to test. Use separate tabs for an
owner/editor revocation scenario.

The response inspector shows status, ETag, and response JSON without recording
the Authorization header or runtime demo-key response. **Reload** explicitly
refreshes an editor after content or sharing changes; conflicts never trigger an
automatic overwrite. Notes are displayed as text, never executed as HTML.

## API

All `/api` routes require `Authorization: Bearer <key>`. `/` and `/health` are
anonymous, but neither exposes notes, keys, or sharing. Responses from API
requests use `Cache-Control: no-store`.

The separate `GET /demo/keys` convenience endpoint is anonymous only in the
explicit local demo mode. It returns `404` when disabled or when the request
does not have a canonical loopback host (`localhost`, `127.0.0.1`, or `::1`),
`X-Notes-Demo: true`, and `Sec-Fetch-Site: same-origin`. An `Origin` header, if
present, must match the request's scheme, host, and port. Successful responses
contain only `{ "keys": { "Alice": "...", "Bob": "...", "Carol": "...",
"Dana": "..." } }` and use `Cache-Control: no-store`. All `/api` authorization,
permission, and concurrency rules remain unchanged.

| Method | Route | Success |
| --- | --- | --- |
| POST | `/api/notes` | `201`, note, `Location`, ETag |
| GET | `/api/notes?limit=20&offset=0` | `200`, accessible summaries and `nextOffset` |
| GET | `/api/notes/{id}` | `200`, note and ETag |
| PUT | `/api/notes/{id}` | `200`, updated note and ETag |
| DELETE | `/api/notes/{id}` | `204`, owner only |
| GET | `/api/notes/{id}/sharing` | `200`, grants and ETag, owner only |
| PUT | `/api/notes/{id}/sharing` | `200`, replacement grants and new ETag, owner only |
| GET | `/api/teams` | `200`, `{items:[{id,name}],nextOffset:null}` |

After loading the generated values into your WSL shell:

```bash
set -a
source .env
set +a

curl -i http://localhost:8080/api/notes \
  -H "Authorization: Bearer $DEMO_ALICE_KEY" \
  -H 'Content-Type: application/json' \
  --data '{"title":"Release checklist","body":"## Before release\n- Review changes"}'

curl -i http://localhost:8080/api/teams \
  -H "Authorization: Bearer $DEMO_ALICE_KEY"
```

Use the returned note ID and Team A ID to replace sharing. Echo the note's
current strong ETag, including its quotes:

```bash
curl -i -X PUT "http://localhost:8080/api/notes/$NOTE_ID/sharing" \
  -H "Authorization: Bearer $DEMO_ALICE_KEY" \
  -H 'Content-Type: application/json' \
  -H 'If-Match: "1"' \
  --data "{\"grants\":[{\"audience\":\"team\",\"teamId\":\"$TEAM_A_ID\",\"permission\":\"edit\"},{\"audience\":\"organization\",\"permission\":\"read\"}]}"

curl -i "http://localhost:8080/api/notes/$NOTE_ID" \
  -H "Authorization: Bearer $DEMO_BOB_KEY"
curl -i "http://localhost:8080/api/notes/$NOTE_ID" \
  -H "Authorization: Bearer $DEMO_CAROL_KEY"
```

The owner always has full control. Edit includes read but never grants deletion,
ownership transfer, or sharing administration. Organization grants include every
authenticated user, including a user with no teams; they never grant anonymous
access. Inaccessible and nonexistent notes both return `404`.

Versions start at 1 and cover both content and sharing. Every successful PUT
increments the version once. PUT and DELETE require exactly one canonical strong
numeric `If-Match`: missing is `428`, stale is `412`, and weak, wildcard, list,
unquoted, noncanonical, or otherwise invalid tags are `400`. After `412`, fetch
the current note and explicitly reconcile before resubmitting; the server never
merges or silently retries writes.

Content requests contain only `title` and `body`. Titles are trimmed and limited
to 1-200 UTF-16 code units; bodies are required strings, may be empty, and are
limited to 64 KiB of decoded UTF-8. Overall request bodies are limited to 1 MiB,
including streamed bodies; HTTP chunk framing does not count toward this
decoded-body limit. Invalid fields, IDs, grants, or pagination values
produce Problem Details. Permission checks precede resource-specific precondition
information. Unexpected errors produce sanitized `500` responses with
server-side diagnostics rather than exposing credentials or note bodies.

## Automated tests

The suite contains pure unit tests and integration tests against **real
PostgreSQL and loopback Kestrel**. Integration tests apply the committed
migrations, enforce database constraints, and exercise actual HTTP requests,
including streamed request limits and concurrent writes.

Run everything using an isolated PostgreSQL 17 test container:

```bash
docker compose --profile test run --build --rm tests
```

The test database is separate from the demo, uses temporary storage, and is
published only on loopback port 15432 for optional host-based test runs. Each
integration fixture creates and drops only its own random database. Missing or
unreachable test configuration fails explicitly; tests are not silently skipped.
Results are saved under `TestResults/notes-tests.trx`.

After testing, remove only the disposable test database container:

```bash
docker compose --profile test rm -sf test-db
```

For fast, database-free unit tests with a .NET 10 SDK:

```bash
dotnet test tests/Notes.Api.Tests/Notes.Api.Tests.csproj --filter Category=Unit
```

Or use the SDK container without starting a database:

```bash
docker compose --profile test run --build --rm --no-deps tests --filter Category=Unit
```

To run the integration suite from a host SDK:

```bash
docker compose --profile test up -d test-db
source .env
export NOTES_TEST_DB="Host=127.0.0.1;Port=${NOTES_TEST_PORT:-15432};Database=postgres;Username=notes_test;Password=$POSTGRES_PASSWORD"
dotnet test tests/Notes.Api.Tests/Notes.Api.Tests.csproj
```

See [the test guide](tests/Notes.Api.Tests/README.md) for coverage and isolation.
The concurrency tests include a database-lock-ordered revocation race, not just
sequential stale-version requests. Application-restart tests preserve the same
database; they deliberately do not restart arbitrary shared PostgreSQL servers.

## Implementation and tradeoffs

There is one application project and one test project, without repository/CQRS
frameworks or mutable in-memory authorization state. A shared EF query computes
access and its associated version in one database snapshot. Listing filters in
PostgreSQL before pagination and orders by `createdAt` then UUID, descending.
Writes compare and increment the version in SQL; sharing replacement runs in the
same transaction as its version update. This prevents an editor using a
pre-revocation snapshot from overwriting a completed revocation.

The relational schema enforces audience/permission values, team-versus-
organization targets, grant uniqueness (including null organization targets),
positive versions, foreign keys, and cascading grants. The committed migration
and snapshot live under `src/Notes.Api/Data/Migrations`. To create later schema
changes, restore the pinned EF tool with `dotnet tool restore`.

For direct non-container execution, configure `ConnectionStrings__Notes` and
`Demo__ApiKeys__Alice`, `Demo__ApiKeys__Bob`, `Demo__ApiKeys__Carol`, and
`Demo__ApiKeys__Dana`, then run the application project with a .NET 10 SDK.
`Database__ApplyMigrations=false` disables startup migration application, not
seeding, when schema deployment is handled separately.

This is a single-organization, single-instance demo. Startup migrations are
intentional for that deployment; multiple replicas require a separate migration
step and consistent identity configuration. API keys omit account lifecycle and
production identity-provider integration. Plain HTTP is acceptable only on
loopback; remote deployment requires TLS or the documented SSH tunnel.

Offset pagination is simple but deep pages cost more and concurrent insertions
can shift boundaries. There is no history, ownership transfer, multi-tenancy,
real-time editing, automatic merge, or measured throughput claim. Multi-tenancy
would require an explicit isolation migration, not another grant type.

If NuGet is unavailable on your network, set `NUGET_SOURCE` in `.env` to the
official Microsoft public mirror shown in `.env.example` and rebuild. Do not
disable TLS verification to work around networking or certificate problems.
