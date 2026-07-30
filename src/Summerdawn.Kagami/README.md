# Summerdawn.Kagami

Kagami is a contact and calendar event synchronization CLI tool with internal SQLite state.

## Overview

Kagami can be used as a library or as a command-line tool. It synchronizes items between configured endpoints, stores cursors and link state in SQLite, and can run once or continuously at a user-specified interval.

### Features

- Google People API contacts
- Google Calendar events
- Microsoft Graph / Exchange Online contacts
- Microsoft Graph / Exchange Online calendar events
- Contact categories / labels
  - Graph `categories`
  - Google contact-group memberships / labels
- Interactive CLI commands to list, export, import, and sync contacts and events
- OData-style in-memory event filters for list, export, import, and sync operations
- OData-style in-memory contact filters for list, export, import, and sync operations
- `--force` mode to unconditionally rewrite all in-scope contacts, bypassing version and content checks — for edge cases only, not normal runs
- `--what-if` mode that logs planned create / update / delete operations without writing changes
- `--interval` mode on `contacts sync` and `events sync` to repeat the sync in-process with a fixed delay between runs
- Cached Google OAuth tokens for repeat runs after the initial interactive sign-in

## Getting Started

The fastest way to get started is to install Kagami as a .NET tool, create a settings file, and run one of the CLI commands:

```bash
dotnet tool install --global Summerdawn.Kagami
kagami --help
kagami contacts --help
kagami events --help
```

If you are developing from source instead, run:

```bash
dotnet run --project src/Summerdawn.Kagami -- --help
dotnet run --project src/Summerdawn.Kagami -- contacts --help
dotnet run --project src/Summerdawn.Kagami -- events --help
```

## Installation

### As a .NET tool

```bash
dotnet tool install --global Summerdawn.Kagami
```

### As a library

```bash
dotnet add package Summerdawn.Kagami
```

## Usage

### Library registration

Register Kagami services against the `Kagami` configuration section:

```csharp
using Microsoft.Extensions.Hosting;
using Summerdawn.Kagami.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddKagami(builder.Configuration.GetSection("Kagami"));
```

This registers the sync engine, persistence services, and a named `HttpClient` plus keyed `IConnector` singleton for each configured endpoint. Connectors are resolved by endpoint name via `Func<string, IConnector<TItem>>`.

### CLI commands

Kagami exposes three top-level command groups:

```text
# Contact operations
kagami contacts list   --from=<endpoint> [--filter=<expr>] [--all]
kagami contacts export --from=<endpoint> --to=<dir> [--filter=<expr>] [--prune]
kagami contacts import --from=<dir> --to=<endpoint> [--prune] [--filter=<expr>] [--what-if|--confirm]
kagami contacts sync   --from=<endpoint> --to=<endpoint> [--bidirectional|--reverse]
                       [--prune] [--on-conflict=last-write-wins|source-wins|destination-wins|skip]
                       [--what-if|--confirm] [--filter=<expr>] [--force] [--interval=<ISO8601>]

# Calendar event operations
kagami events list     --from=<endpoint> [--filter=<expr>] [--all]
kagami events export   --from=<endpoint> --to=<dir> [--filter=<expr>] [--prune]
kagami events import   --from=<dir> --to=<endpoint> [--prune] [--filter=<expr>] [--force] [--what-if|--confirm]
kagami events sync     --from=<endpoint> --to=<endpoint> [--bidirectional|--reverse]
                       [--prune] [--on-conflict=last-write-wins|source-wins|destination-wins|skip]
                       [--full] [--what-if|--confirm] [--filter=<expr>] [--force] [--interval=<ISO8601>]

# Job admin / housekeeping
kagami jobs list
kagami jobs reset  --key <jobKey>
kagami jobs reset  --all
kagami jobs unlock --key <jobKey>
kagami jobs unlock --all
```

All CLI commands accept `--settings`, `--no-default-settings`, and `--verbose`. Run a command with `--help` for its operation-specific options.

#### `kagami contacts list`

Fetch and display contacts from a configured endpoint. By default, `kagami contacts list` displays up to 100 matching contacts; use `--all` to display everything.

```bash
kagami contacts list --from Microsoft
kagami contacts list --from Microsoft --all
kagami contacts list --from Microsoft --filter "startswith(name,'A')"
kagami contacts list --from Microsoft --filter "contains(categories,'Recruiter')"
```

#### `kagami contacts export`

Export contacts as one JSON file per contact into a local directory. Existing export files are preserved by default; when `--prune` is specified, stale `*.json` files and previously exported photo files that are no longer present in the source set are removed.

```bash
kagami contacts export --from Microsoft --to ./export --settings appsettings.json
kagami contacts export --from Microsoft --to ./export --prune --settings appsettings.json
```

#### `kagami contacts import`

Import contacts from local JSON files in a directory into a configured endpoint.

```bash
# Import all contacts from a local directory
kagami contacts import --from ./export --to Google

# Import with prune: remove destination contacts not present in the import set
kagami contacts import --from ./export --to Google --prune

# Dry run
kagami contacts import --from ./export --to Google --what-if
```

When `--prune` is specified, Kagami deletes destination contacts that did not appear in the import set.

#### `kagami contacts sync`

Synchronize contacts between two configured endpoints.

```bash
# Forward sync (default: source to destination)
kagami contacts sync --from Microsoft --to Google

# Bidirectional sync
kagami contacts sync --from Microsoft --to Google --bidirectional

# Reverse sync
kagami contacts sync --from Microsoft --to Google --reverse

# One-directional with prune (mirror mode)
kagami contacts sync --from Microsoft --to Google --prune

# Dry run: log planned actions without writing anything
kagami contacts sync --from Microsoft --to Google --what-if

# Force: re-evaluate all in-scope contacts even if unchanged
kagami contacts sync --from Microsoft --to Google --force

# Filter: only synchronize contacts whose effective name starts with 'A'
kagami contacts sync --from Microsoft --to Google --filter "startswith(name,'A')"

# Filter: only synchronize contacts in the Recruiter category
kagami contacts sync --from Microsoft --to Google --filter "contains(categories,'Recruiter')"
```

- `--bidirectional`: sync in both directions; otherwise changes flow from `--from` to `--to`
- `--reverse`: sync from `--to` back to `--from`
- `--prune`: delete destination contacts that no longer exist on the source (or vice versa)
- `--on-conflict`: `last-write-wins` (default), `source-wins`, `destination-wins`, or `skip`
- `--full`: ignore saved cursors and fetch all rows from both sides, but still skip contacts whose payload and photo are already identical on both sides
- `--force`: fetch all rows, bypass all change and sameness checks for every in-scope contact, and write unconditionally — use only when normal change detection via version/hash is known to be unreliable (e.g. destination data drifted outside the canonical model). Do not use for normal runs.
- `--filter`: apply an OData-style filter in memory before planning or writing changes
- `--interval`: ISO 8601 duration (e.g. `PT15M`, `PT2H`). When specified, the sync repeats indefinitely with the given delay between runs; without it the command runs once and exits. If a run fails, the process exits nonzero immediately (works well with Docker/container restart policies).

#### `kagami events list`

Fetch and display calendar events from a configured endpoint. By default, `kagami events list` displays up to 100 matching events; use `--all` to display everything.

```bash
kagami events list --from WorkCalendar --settings appsettings.json
kagami events list --from WorkCalendar --all --settings appsettings.json
kagami events list --from WorkCalendar --filter "startswith(title,'Team')" --settings appsettings.json
```

#### `kagami events export`

Export events as one JSON file per event into a local directory. Filenames are derived from a sanitized, truncated event title plus start date/time. Existing export files are preserved by default; when `--prune` is specified, stale event JSON files are removed.

```bash
kagami events export --from WorkCalendar --to ./event-export --settings appsettings.json
kagami events export --from WorkCalendar --to ./event-export --prune --settings appsettings.json
```

#### `kagami events import`

Import events from local JSON files in a directory into a configured endpoint.

```bash
# Import all events from a local directory
kagami events import --from ./event-export --to ArchiveCalendar --settings appsettings.json

# Import with prune: remove destination events not present in the import set
kagami events import --from ./event-export --to ArchiveCalendar --prune --settings appsettings.json

# Dry run
kagami events import --from ./event-export --to ArchiveCalendar --what-if --settings appsettings.json
```

When `--prune` is specified, Kagami deletes destination events that did not appear in the import set.

#### `kagami events sync`

Synchronize calendar events between two configured endpoints.

```bash
# Forward sync (default: source to destination)
kagami events sync --from WorkCalendar --to ArchiveCalendar --settings appsettings.json

# Bidirectional sync
kagami events sync --from WorkCalendar --to ArchiveCalendar --bidirectional --settings appsettings.json

# Reverse sync
kagami events sync --from WorkCalendar --to ArchiveCalendar --reverse --settings appsettings.json

# One-directional with prune (mirror mode)
kagami events sync --from WorkCalendar --to ArchiveCalendar --prune --settings appsettings.json

# Dry run: log planned actions without writing anything
kagami events sync --from WorkCalendar --to ArchiveCalendar --what-if --settings appsettings.json

# Force: re-evaluate all in-scope events even if unchanged
kagami events sync --from WorkCalendar --to ArchiveCalendar --force --settings appsettings.json

# Filter: only synchronize events whose title starts with 'Team'
kagami events sync --from WorkCalendar --to ArchiveCalendar --filter "startswith(title,'Team')" --settings appsettings.json
```

- `--bidirectional`: sync in both directions; otherwise changes flow from `--from` to `--to`
- `--reverse`: sync from `--to` back to `--from`
- `--prune`: delete destination events that no longer exist on the source (or vice versa)
- `--on-conflict`: `last-write-wins` (default), `source-wins`, `destination-wins`, or `skip`
- `--full`: ignore saved cursors and fetch all rows from both sides, but still skip events whose content is already identical on both sides
- `--force`: fetch all rows, bypass all change and sameness checks for every in-scope event, and write unconditionally — use only when normal change detection via version/hash is known to be unreliable. Do not use for normal runs.
- `--filter`: apply an OData-style filter in memory before planning or writing changes
- `--interval`: ISO 8601 duration (e.g. `PT15M`, `PT2H`). When specified, the sync repeats indefinitely with the given delay between runs; without it the command runs once and exits. If a run fails, the process exits nonzero immediately (works well with Docker/container restart policies).

### `kagami jobs` commands

The `jobs` command group provides operational/admin access to persisted job state. Sync job keys use the canonical format `{category}:{from}:{to}` (for example `contacts:Microsoft:Google` or `events:WorkCalendar:ArchiveCalendar`). Import and export runs also create job keys in the format `{category}:{operation}:{left}:{right}`.

#### `kagami jobs list`

List all known jobs and their current lock state. A job becomes known after its first sync run.

```bash
kagami jobs list
```

Output columns: `Key`, `Type`, `From`, `To`, `Locked`.

#### `kagami jobs reset`

Reset stored sync state (link-state rows, cursors, and locks) for a specific job or for all jobs. Use this after deleting the database or when you need to force a full re-sync.

```bash
# Reset state for a specific job
kagami jobs reset --key contacts:Microsoft:Google

# Reset all jobs
kagami jobs reset --all
```

#### `kagami jobs unlock`

Force-release job locks after an interrupted run to clear stuck locks. This only clears the lock; it does not touch link state or cursors.

```bash
# Unlock a specific job
kagami jobs unlock --key contacts:Microsoft:Google

# Unlock all jobs
kagami jobs unlock --all
```

### Sync flags: `--full` and `--force`

#### `--full` — full enumeration, normal sameness checks

`--full` tells the engine to ignore saved API cursors and enumerate every contact from both sides. Change detection and cross-side content comparison still apply: contacts whose canonical payload **and** photo are already identical on both sides are skipped without a write, but a link record is still created or updated in the database.

Use `--full` when you know some contacts changed but your cursor state is stale (e.g. you deleted the database, or a previous run failed mid-way).

#### `--force` — unconditional writes

`--force` includes everything `--full` does, and additionally bypasses both the `HasChanged` short-circuit and the cross-side content-sameness check for **all** in-scope contacts — including newly inferred pairs. Every in-scope contact is written to the destination regardless of whether it looks identical. Conflict policies still apply.

**Use `--force` only in edge cases where normal change detection is unreliable.** For example:

- Destination data has been modified directly outside Kagami in ways Kagami cannot detect via its version or content-hash comparison (e.g. fields that Kagami manages were edited by another application).
- Contact photos were updated on the source but were not reflected in any detectable version change.
- You suspect a version or hash drift that is preventing changes from being picked up.

**Do not use `--force` for normal runs.** A regular sync already compares content via version number and content hash; there is no benefit to forcing a rewrite when nothing has actually changed, and it produces unnecessary writes on the destination side.

| Flag | Ignores cursors | Skips identical contacts | Respects conflict policy |
|---|---|---|---|
| _(neither)_ | No | Yes | Yes |
| `--full` | Yes | Yes | Yes |
| `--force` | Yes | No — writes unconditionally | Yes |

| Combination | Behavior |
|---|---|
| `--force` (forward) | Re-pushes all in-scope source items to destination. Items on destination not matched by source are unchanged (no deletions unless `--prune` is also set). |
| `--force --prune` (forward) | Mirrors the source completely: items missing from source are deleted on destination, same as a first forward+prune run on empty state. |
| `--bidirectional --force --prune` | Produces **no deletions**. Because `--force` is equivalent to a first run, and a first bidirectional run with `--prune` produces no deletions (there is no prior change log to compare against), the result is the same here. |

#### Filter scope and deletions

When a filter is active (e.g. `--filter "name eq 'Alice'"`) and an item on one side changes such that it no longer satisfies the filter (e.g. the contact is renamed), Kagami treats that item as **deleted within the scope of this sync job**. The same logic applies to items deleted externally that do not appear in a full scan.

| Scenario | Result |
|---|---|
| Source item moves out of filter scope; `--prune` set (forward) | Corresponding destination item is deleted. |
| Source item moves out of filter scope; `--prune` not set | No-op is recorded; the item is left untouched on destination. |
| Destination item moves out of filter scope; `--prune` set (bidirectional or reverse) | Corresponding source item is deleted. |
| Source item deleted externally; `--force --prune` (forward) | Destination item is deleted (mirrors a fresh run). |
| Source item deleted externally; `--force` without `--prune` | No deletion; a subsequent normal run will resolve the state once the filter or remote state is clear. |

### Sync behavior: cursors, full loads, scope, matching, and pruning

Kagami combines **persisted link state**, **provider cursors / delta tokens**, and the **current in-scope contact snapshots** returned by each provider. On a normal incremental run, it reuses the saved cursor on each side and reads only the provider-reported changes since the previous successful sync. In that mode, contacts that are not returned by the provider are treated as **implicitly unchanged**, not deleted. This keeps incremental sync efficient and avoids treating ordinary delta omissions as removals.

When Kagami cannot safely continue from an existing cursor — for example on the first run, after cursor expiry, or when the effective query scope changes — it performs a **full load on both sides** and replaces **both cursors together**. A full load establishes a new baseline for the current scope. Kagami does not compare the current snapshots against every historical link row in the database. Persisted link rows participate in planning only when their source ID appears in the currently loaded source set or their destination ID appears in the currently loaded destination set. If neither side of a persisted link is present in the current loaded sets, that link row is ignored for the run and cannot trigger updates or deletions.

This is what makes scoped full loads safe for pruning. If both providers are queried with the same scope — for example, `--filter "contains(categories,'Recruiter')"` on both sides — then contacts outside that scope on **both** sides are not part of the comparison universe for that run. Even if older link-state rows still exist in the database, they are ignored unless one of their IDs appears in one of the currently loaded sets. Kagami therefore does **not** delete contacts merely because they are outside the current scope.

#### Matching and pruning rules

Kagami first builds links from the currently loaded contacts and the relevant persisted link rows. Existing persisted links are honored when one side is present in the current sets. Remaining unlinked contacts are then matched by the normal contact-matching rules to infer new links where that is safe. Pruning only applies within the current comparison set and only when delete mirroring is enabled, such as with `--prune`.

If a contact is absent from the current run on **both** sides, it is out of scope for that run and is ignored. If a contact is present on one side but not the other, then it is in scope for comparison and Kagami may create, update, or delete the counterpart according to sync direction, conflict policy, and delete policy.

#### Examples

**Example 1: old contact outside the current scope on both sides**

A previous sync linked a contact that did not have the `Recruiter` category. A later full sync is run with `--filter "contains(categories,'Recruiter')"` on both providers. That older contact is returned by neither provider, so neither its source ID nor destination ID appears in the current loaded sets. Its persisted link row is ignored for this run, and Kagami will not delete anything because of it.

**Example 2: scope reset with new cursors**

Suppose a saved cursor can no longer be reused because the effective filter changed. Kagami performs a full load on both sides for the new scope and replaces both cursors together. This creates a fresh baseline for that scope. Because links whose IDs are absent from both loaded sets are ignored, contacts outside the new scope do not participate in prune decisions.

**Example 3: contact present on one side only**

A contact still matches `contains(categories,'Recruiter')` on the destination side but has been deleted from the source side, or no longer matches the source-side filter while still appearing on the destination side. In that case, the persisted link is relevant because one side is still present in the loaded sets. Kagami can then treat that as an in-scope delete or out-of-scope transition and mirror the deletion if pruning is enabled.

#### Safety invariant

> Kagami only plans actions from the current loaded item sets plus persisted links that are touched by those sets.

This means link-state rows for contacts that are outside the current scope on both sides are inert for that run. When both sides are reloaded with the same scope and both cursors are replaced together, pruning remains bounded to the current scope rather than historical data outside it.

### Filter expressions

| Expression | Meaning |
|---|---|
| `startswith(name,'A')` | Effective contact name starts with `A` (case-insensitive) |
| `endswith(name,'son')` | Effective contact name ends with `son` (case-insensitive) |
| `contains(name,'Smith')` | Effective contact name contains `Smith` (case-insensitive) |
| `contains(categories,'Recruiter')` | Contact has a category exactly equal to `Recruiter` (case-insensitive) |
| `name eq 'Alice'` | Effective contact name is exactly `Alice` (case-insensitive) |

`name` maps to the effective contact name: `DisplayName` when present, otherwise `Organization`. `categories` matches exact entries in `CanonicalContact.Categories`, not substrings.

## Configuration

Kagami reads configuration from the `Kagami` section of a settings JSON file.

```json
{
  "Kagami": {
    "DataDirectory": "C:\\Users\\Alice\\AppData\\Local\\Summerdawn.ai\\Kagami",
    "Endpoints": {
      "googleContacts": {
        "Type": "GoogleContacts",
        "Credential": {
          "Type": "GoogleOAuthCredential",
          "ClientId": "your-google-client-id.apps.googleusercontent.com",
          "ClientSecret": "replace-me"
        },
        "Properties": {}
      },
      "googleCalendar": {
        "Type": "GoogleCalendar",
        "Credential": {
          "Type": "GoogleOAuthCredential",
          "ClientId": "your-google-client-id.apps.googleusercontent.com",
          "ClientSecret": "replace-me"
        },
        "Properties": {
          "calendarId": "primary"
        }
      },
      "exchangeContacts": {
        "Type": "MicrosoftContacts",
        "Credential": {
          "Type": "MicrosoftClientCredential",
          "TenantId": "00000000-0000-0000-0000-000000000000",
          "ClientId": "11111111-1111-1111-1111-111111111111",
          "ClientSecret": "replace-me"
        },
        "Properties": {
          "userId": "person@summerdawn.ai"
        }
      },
      "exchangeCalendar": {
        "Type": "MicrosoftCalendar",
        "Credential": {
          "Type": "MicrosoftClientCredential",
          "TenantId": "00000000-0000-0000-0000-000000000000",
          "ClientId": "11111111-1111-1111-1111-111111111111",
          "ClientSecret": "replace-me"
        },
        "Properties": {
          "userId": "person@summerdawn.ai"
        }
      }
    }
  }
}
```

### Configuration Precedence

Kagami uses [.NET Configuration providers](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration) to load settings from multiple sources in a specific order, with later sources overriding earlier ones:

1. **Embedded default settings** - Built-in defaults embedded in the application, unless skipped with `--no-default-settings`
2. **Content-directory settings files** - `appsettings.json` in the current working directory, if present
3. **Application-data settings file** - `%LOCALAPPDATA%\Summerdawn.ai\Kagami\appsettings.json`, if present
4. **Environment variables** - System or process environment variables
5. **Explicit settings files** - Additional settings files specified with the `--settings` option, in argument order
6. **Verbose logging settings** - Embedded logging settings, when `--verbose` is specified

Use `--settings` to run with a configuration file outside the standard locations. Set `Kagami:DataDirectory` to store `sync.db` in a nonstandard directory; it does not change where `appsettings.json` is loaded from.

### `Endpoints`

Named endpoint definitions. Each endpoint includes its credential inline.

#### `GoogleContacts`

Credential fields (`Type = "GoogleOAuthCredential"`):

- `ClientId`: Google OAuth client ID
- `ClientSecret`: Google OAuth client secret

Endpoint properties:

- none required

Notes:

- On first use, Kagami opens the browser for OAuth consent and listens on `http://localhost:4189/` for the callback
- Access and refresh tokens are cached in the `tokens` subdirectory of `Kagami:DataDirectory`
- Kagami requests the Google contacts scope `https://www.googleapis.com/auth/contacts`
- The built-in Google contacts connector currently uses end-user OAuth; service-account and domain-wide-delegation auth are not supported

#### `GoogleCalendar`

Credential fields (`Type = "GoogleOAuthCredential"`):

- `ClientId`: Google OAuth client ID
- `ClientSecret`: Google OAuth client secret

Endpoint properties:

- `calendarId`: optional; defaults to `primary`

Notes:

- On first use, Kagami opens the browser for OAuth consent and listens on `http://localhost:4189/` for the callback
- Access and refresh tokens are cached in the `tokens` subdirectory of `Kagami:DataDirectory`
- Kagami requests the Google Calendar scope `https://www.googleapis.com/auth/calendar`
- Birthday and other non-default special event types are filtered out; only regular calendar events are synchronized

#### `MicrosoftContacts`

Credential fields (`Type = "MicrosoftClientCredential"`):

- `TenantId`: Entra tenant ID
- `ClientId`: app registration client ID
- `ClientSecret`: client secret for MVP setups
- `CertificatePath`: optional PFX/PKCS#12 certificate path for long-term unattended use
- `CertificatePassword`: optional certificate password

Use either `ClientSecret` or `CertificatePath` (+ `CertificatePassword` if needed).

Endpoint properties:

- `userId`: required; the mailbox owner to access, typically a user principal name or user ID
- `folderId`: optional contact folder ID; if omitted, Kagami uses the default contacts collection

Behavior:

- reads and writes Microsoft Graph contacts
- maps Outlook categories to canonical contact categories / labels

#### `MicrosoftCalendar`

Credential fields (`Type = "MicrosoftClientCredential"`):

- `TenantId`: Entra tenant ID
- `ClientId`: app registration client ID
- `ClientSecret`: client secret for MVP setups
- `CertificatePath`: optional PFX/PKCS#12 certificate path for long-term unattended use
- `CertificatePassword`: optional certificate password

Use either `ClientSecret` or `CertificatePath` (+ `CertificatePassword` if needed).

Endpoint properties:

- `userId`: required; the mailbox owner to access, typically a user principal name or user ID
- `calendarId`: optional calendar ID; if omitted, Kagami uses the default calendar collection

Behavior:

- reads and writes Microsoft Graph events
- uses the Microsoft Graph v1.0 `/events/delta` endpoint for incremental polling

## State Database

Kagami stores its SQLite state database as `sync.db` in `Kagami:DataDirectory`. The directory is created automatically on first run.

## Authentication Setup

### Microsoft Graph / Exchange Online

Minimal unattended setup:

1. Create a single-tenant app registration in your Entra tenant
2. Grant the required application permissions
3. Grant admin consent
4. Configure Kagami with either a client secret or, preferably, a certificate

Required Graph permission for write sync:

- `Contacts.ReadWrite` (Application)
- `Calendars.ReadWrite` (Application) for calendar event sync

Kagami uses app-only access against:

- `/users/{userId}/contacts`
- `/users/{userId}/contactFolders/{folderId}/contacts`
- `/users/{userId}/events`
- `/users/{userId}/calendars/{calendarId}/events`

### Google Workspace / People API and Google Calendar API

Minimal setup:

1. Create a Google Cloud project
2. Enable the People API
3. Enable the Google Calendar API when syncing events
4. Create an OAuth 2.0 client for a desktop or installed application
5. Configure the loopback callback `http://localhost:4189/`
6. Configure Kagami with `clientId` and `clientSecret`
7. Run a Google-backed Kagami command once and complete the browser sign-in flow

Kagami uses:

- Google OAuth 2.0 authorization-code flow with a local loopback callback
- cached access and refresh tokens for subsequent runs
- the People API contacts scope for contact endpoints
- the Google Calendar scope for calendar endpoints
- `people.connections.list` sync tokens for incremental polling
- Google Calendar `events.list` sync tokens for incremental polling on calendar endpoints

## Contact Mapping Notes

Canonical contact fields currently include:

- names
- emails
- phones
- addresses
- organization
- job title
- notes
- birthday
- categories / labels

Label/category mapping:

- **Microsoft Graph**: `CanonicalContact.Categories` ⇄ `contact.categories`
- **Google**: `CanonicalContact.Categories` ⇄ contact-group memberships / labels

## Calendar Event Mapping Notes

Canonical event fields currently include:

- title
- description
- start / end date-time
- location
- organizer
- attendees
- recurrence
- iCal UID

Provider notes:

- **Google Calendar** syncs regular calendar events only (`eventTypes=default`) and skips special event types such as birthdays
- **Microsoft Graph** maps event timestamps through the API's `dateTimeTimeZone` payloads and uses Graph event delta tokens for incremental polling

## Event Filter Expressions

| Expression | Meaning |
|---|---|
| `startswith(title,'Tea')` | Event title starts with `Tea` (case-insensitive) |
| `endswith(title,'Sync')` | Event title ends with `Sync` (case-insensitive) |
| `contains(title,'Team')` | Event title contains `Team` (case-insensitive) |
| `title eq 'Planning'` | Event title is exactly `Planning` (case-insensitive) |

Event filters currently apply to the canonical event title only.

## Error Handling

### Fault policy for sync runs

Each sync pass applies a conservative fault policy designed to ensure **no silent gaps** in sync coverage.

#### Transient failures (HTTP 429 and 5xx)

When a connector returns a rate-limit (HTTP 429) or any server error (5xx), Kagami treats this as a **transient provider failure** and aborts the entire run immediately:

- No further items are processed.
- The operation is marked faulted.
- **Cursors are not advanced.**

On the next scheduled run, Kagami retries from the same cursor position. Because already-synced items are stored with a version hash in the link-state database, they are recognised as unchanged on replay and skipped cheaply — the replay cost is primarily scanning, not duplicate writes.

#### Permanent per-item failures (other non-HTTP and 4xx exceptions)

For non-transient failures (any exception that is not a 429 or 5xx), Kagami:

- Logs an error for the affected item and writes an `"error"` entry to the operation log.
- Continues processing remaining items in the batch.
- Emits a final warning after the pass summarising that one or more actions failed.
- **Cursors are advanced**, so the bad item is not retried on every subsequent run.

To recover: either fix the bad source data (and wait for the item to appear in the next delta) or run a `--full` sync to reprocess all items.

#### Cursor advancement

| Run outcome | Cursor advanced? |
|---|---|
| All items succeeded | ✅ Yes |
| Transient provider failure (429 / 5xx) | ❌ No — run aborted immediately |
| One or more permanent per-item failures | ✅ Yes — pass completes; a summary warning is logged |
| What-if run | ❌ No — no writes performed |

#### Partial writes

Link-state updates written before a transient fault are left in place and are not rolled back. On replay, items already present in the link-state table with a matching version hash are skipped automatically.

## Current Limitations

- Built-in concrete provider connectors support contacts (Google People, Microsoft Exchange) and calendar events (Google Calendar, Microsoft Calendar)
- What-if mode logs planned operations but intentionally does not update cursors or link state

## Development

```bash
dotnet build
dotnet test
```
