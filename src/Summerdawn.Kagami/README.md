# Summerdawn.Kagami

Kagami is a contact and calendar event synchronization CLI tool with internal SQLite state.

## Overview

Kagami supports listing, importing, exporting, and synchronizing contacts and calendar events from Microsoft Graph and Google accounts.

## Getting Started

1. Create and authorize Microsoft and Google applications as described in [Authorization](#authorization).
2. Add the application credentials and endpoints to [Configuration](#configuration).
3. Install Kagami and run a command:

   ```bash
   dotnet tool install --global Summerdawn.Kagami
   kagami --help
   kagami contacts sync --from Microsoft --to Google
   kagami events sync --from WorkCalendar --to ArchiveCalendar
   ```

See [Usage](#usage) for the command groups and [Configuration](#configuration) for the complete settings reference.

## Installation

### As a .NET Tool

```bash
dotnet tool install --global Summerdawn.Kagami
```

Or install it locally in a project:

```bash
dotnet tool install Summerdawn.Kagami
```

The .NET tool requires the .NET 10 runtime or later.

### As a Standalone Binary

Download a platform-specific, self-contained AOT binary from Kagami's [GitHub Releases](https://github.com/summerdawn-ai/kagami/releases). Supported platforms are Windows (x64 and ARM64), Linux (x64 and ARM64), and macOS (x64 and ARM64). Standalone binaries do not require a separate .NET runtime installation.

### Running from the Package with dnx

With the .NET 10 SDK or later, run Kagami directly from its package without installing it globally or locally:

```bash
dotnet tool exec Summerdawn.Kagami --yes -- sync --settings mydir/appsettings.json

# Or simply
dnx Summerdawn.Kagami --yes -- sync --settings mydir/appsettings.json
```

See the [dotnet tool exec documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec) for more information.

## Usage

Kagami provides commands for listing, importing, exporting, and synchronizing contacts and calendar events, as well as inspecting jobs and managing endpoint authentication.

Kagami provides four command groups:

#### Contacts

Use `contacts` to list, export, import, and synchronize contacts.

```bash
kagami contacts list --from Microsoft
kagami contacts export --from Microsoft --to ./contacts
kagami contacts import --from ./contacts --to Google
kagami contacts sync --from Microsoft --to Google
```

Use `kagami contacts --help` for operation-specific options such as `--filter`, `--prune`, `--what-if`, `--full`, and `--force`.

#### Events

Use `events` to list, export, import, and synchronize calendar events.

```bash
kagami events list --from WorkCalendar
kagami events export --from WorkCalendar --to ./events
kagami events import --from ./events --to ArchiveCalendar
kagami events sync --from WorkCalendar --to ArchiveCalendar
```

Use `kagami events --help` for operation-specific options.

#### Jobs

Use `jobs` to inspect and reset persisted synchronization state:

```bash
kagami jobs list
kagami jobs reset --key contacts:Microsoft:Google
kagami jobs reset --all
kagami jobs unlock --key contacts:Microsoft:Google
```

#### Endpoints

Use `endpoints` to inspect configured endpoints and manage cached Google OAuth credentials:

```bash
kagami endpoints list
kagami endpoints login --endpoint Google
kagami endpoints logout --endpoint Google
```

Microsoft client credentials do not use interactive login. Run any command with `--help` to see its complete option set.

#### Common Flags

Common flags for some or all commands include:

- `--settings`: The path to any additional configuration JSON file to load; can be specified multiple times.
- `--what-if` plans and logs create, update, and delete operations during sync or import without writing changes or advancing synchronization cursors.
- `--verbose` enables more detailed application and HTTP client logging for the current run. See [Verbose Logging](#verbose-logging) for the logging configuration it applies.

## Synchronization

Kagami is a synchronization engine with change detection, persisted cursors, link state, conflict policies, scoped pruning, and optional continuous execution. It normalizes provider data into canonical contacts and events before comparing the current item sets with previously synchronized state.

On a normal incremental run, Kagami reuses provider delta or sync tokens when available and reads only changes since the previous successful synchronization. When a cursor cannot be reused, such as on the first run, after cursor expiry, or after a scope change, Kagami performs a full load for the current scope and establishes a new baseline. Persisted links outside the current loaded scope are ignored.

Synchronization is source-to-destination by default. Use `--bidirectional` to synchronize both directions or `--reverse` to synchronize from `--to` back to `--from`. Conflict behavior is controlled with `--on-conflict`:

- `last-write-wins` (default)
- `source-wins`
- `destination-wins`
- `skip`

### Pruning

`--prune` enables delete mirroring within the current synchronization scope. Without it, items absent from the source are left on the destination. Filtering limits pruning to the current comparison scope; items outside that scope are not deleted.

### Full Loads

`--full` ignores saved provider cursors and enumerates all items in the current scope. Normal version and canonical-content comparisons still prevent unnecessary writes. Use it when cursor state is stale or after correcting data or configuration.

For event full reads without an applicable delta cursor, Kagami uses a rolling one-year lookback by default. Incremental event runs continue from their saved provider cursors.

### Forced Writes

`--force` includes a full enumeration and bypasses change and content-sameness checks for every in-scope item. It writes unconditionally, while still applying the selected conflict policy. Use it only when normal version or content-hash detection is known to be unreliable; it is not intended for normal runs.

| Mode | Ignores cursors | Skips identical items | Writes all in-scope items |
|---|---:|---:|---:|
| Default | No | Yes | No |
| `--full` | Yes | Yes | No |
| `--force` | Yes | No | Yes |

### Continuous Sync

`--interval` repeats synchronization indefinitely using an ISO 8601 duration such as `PT15M` or `PT2H`; without it, the command runs once and exits. If a repeated run fails, the process exits with a nonzero status.

### State

Kagami stores synchronization state in SQLite at `<DataDirectory>/sync.db`. The sync database does not store event or contact details, credentials, or other provider payloads. For each synchronized item, it stores only synchronization metadata:

- provider identifiers for the source and destination items
- provider version values and canonical-content hashes
- last-seen and last-synchronized timestamps
- deletion, origin, conflict, and synchronization-result status
- the job and endpoint identifiers needed to partition state

The database also stores opaque provider cursors or delta tokens, job locks, and operation-log entries containing operation metadata. This state lets later runs detect changes, avoid duplicate writes, and resume incrementally after successful or interrupted runs.

Deleting the database resets synchronization history. The next run performs a new baseline synchronization for the current scope.

### Scope and Matching

Kagami builds links from the current loaded items and relevant persisted link records, then matches remaining unlinked items using provider-independent canonical fields. An item present on only one side is in scope for comparison and may be created, updated, or deleted according to direction, conflict, and prune settings. An item absent from both sides is outside the current run and cannot trigger an action.

This scope rule makes filtered full loads safe: a contact or event that does not match the active filter on either side does not participate in pruning merely because an older link record exists in the database.

When an item moves out of the active filter scope, Kagami treats it as deleted within that synchronization scope. With `--prune`, the corresponding item on the other side is deleted; without `--prune`, it is left untouched. The same rules apply when an item is externally deleted and is absent from a full scan.

> Kagami only plans actions from the current loaded item sets and persisted links touched by those sets. Link records for items outside the current scope on both sides are inert for that run.

## Filtering

Filters are evaluated in memory before planning or writing changes. Contact and event filters support a flat, case-insensitive list of expressions joined with `and`.

### Contact Filters

| Expression | Meaning |
|---|---|
| `startswith(name,'A')` | Effective contact name starts with `A` |
| `endswith(name,'son')` | Effective contact name ends with `son` |
| `contains(name,'Smith')` | Effective contact name contains `Smith` |
| `contains(categories,'Recruiter')` | Contact has an exact `Recruiter` category |
| `name eq 'Alice'` | Effective contact name is exactly `Alice` |

`name` uses `DisplayName` when present and otherwise `Organization`. Category matching is exact and case-insensitive.

### Event Filters

| Expression | Meaning |
|---|---|
| `startswith(title,'Tea')` | Event title starts with `Tea` |
| `endswith(title,'Sync')` | Event title ends with `Sync` |
| `contains(title,'Team')` | Event title contains `Team` |
| `title eq 'Planning'` | Event title is exactly `Planning` |
| `start gt '2026-01-01T00:00:00Z'` | Event starts after the timestamp |
| `end lt '2026-02-01T00:00:00Z'` | Event ends before the timestamp |

Date comparisons are strict and use ISO 8601 timestamps. For example:

```bash
kagami events sync --from WorkCalendar --to ArchiveCalendar --filter "contains(title,'Team') and start gt '2026-01-01T00:00:00Z' and end lt '2026-02-01T00:00:00Z'"
```

## Authorization

Kagami uses provider-specific application credentials. Microsoft uses non-delegated application permissions; Google uses an OAuth 2.0 authorization-code flow with a local callback and cached tokens. Kagami does not provide delegated Microsoft login.

### Microsoft Graph

Create a single-tenant application registration in [Microsoft Entra](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app), add administrator consent for these **Application** permissions, and create either a client secret or certificate:

- `Contacts.ReadWrite`
- `Calendars.ReadWrite`

Configure the tenant id, client id, and client secret or certificate in the Microsoft credential. Kagami accesses the mailbox identified by the endpoint `userId`.

See the [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference) for permission details.

### Google

Create a project in [Google Cloud](https://console.cloud.google.com/), enable the [People API](https://console.cloud.google.com/apis/library/people.googleapis.com) for contacts and the [Google Calendar API](https://console.cloud.google.com/apis/library/calendar-json.googleapis.com) for events. Create an OAuth 2.0 client for a desktop or installed application and provide its client id and client secret.

Kagami uses these scopes:

- Contacts: `https://www.googleapis.com/auth/contacts`
- Calendar events: `https://www.googleapis.com/auth/calendar`

The first Google command opens a browser and uses the loopback callback `http://localhost:4189/`. Access and refresh tokens are cached in the `tokens` directory under `DataDirectory` for subsequent runs. See Google's [OAuth 2.0 for installed applications](https://developers.google.com/identity/protocols/oauth2/native-app) documentation.

## Configuration

Kagami binds the `Kagami` section to `KagamiOptions`:

```jsonc
{
  "Kagami": {
    "DataDirectory": "C:\\Users\\Alice\\AppData\\Local\\Summerdawn.ai\\Kagami",
    "Endpoints": {
      "Google": {
        "Type": "Google",
        "Credential": {
          "Type": "GoogleOAuthCredential",
          "ClientId": "your-google-client-id.apps.googleusercontent.com",
          "ClientSecret": "replace-me"
        },
        "Properties": {
          "userId": "alice@example.com",
          "calendarId": "primary"
        }
      },
      "Microsoft": {
        "Type": "Microsoft",
        "Credential": {
          "Type": "MicrosoftClientCredential",
          "TenantId": "00000000-0000-0000-0000-000000000000",
          "ClientId": "11111111-1111-1111-1111-111111111111",
          "ClientSecret": "replace-me"
        },
        "Properties": {
          "userId": "alice@example.com",
          "calendarId": "calendar-id"
        }
      }
    }
  }
}
```

### Configuration Precedence

Kagami uses [.NET Configuration providers](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration) to load settings from multiple sources in a specific order, with later sources overriding earlier ones.

1. **Embedded default settings** - Built-in defaults embedded in the application, unless skipped with `--no-default-settings`
2. **Content-directory settings files** - `appsettings.json` in the current working directory, if present
3. **Application-data settings file** - `%LOCALAPPDATA%\Summerdawn.ai\Kagami\appsettings.json`, if present
4. **Environment variables** - System or process environment variables
5. **Explicit settings files** - Additional settings files specified with the `--settings` option, in argument order
6. **Verbose logging settings** - Embedded logging settings, when `--verbose` is specified

Setting `Kagami:DataDirectory` to a nonstandard directory does not change where the standard application settings files are loaded from.

### Default Settings

The CLI applies these embedded default settings to every run unless default settings are disabled. They establish the normal logging levels and suppress noisy HTTP client and Polly logs:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "System.Net.Http.HttpClient": "Warning",
      "Polly": "Warning"
    }
  }
}
```

### Configuration Reference

#### Root

| Name | Type | Description | Example |
|---|---|---|---|
| `DataDirectory` | `string` | Directory for `sync.db` and cached Google tokens | `C:\Users\Alice\AppData\Local\Summerdawn.ai\Kagami` |
| `Endpoints` | `object` | Named endpoint definitions | `{ "Google": { ... } }` |

#### Endpoints

Each endpoint has a `Type`, an inline `Credential`, and provider-specific `Properties`. Use `GoogleContacts`, `GoogleEvents`, `MicrosoftContacts`, or `MicrosoftEvents` when an endpoint should expose only one resource; use `Google` or `Microsoft` to expose both.

| Name | Type | Description |
|---|---|---|
| `Type` | `string` | `Google`, `GoogleContacts`, `GoogleEvents`, `Microsoft`, `MicrosoftContacts`, or `MicrosoftEvents` |
| `Credential` | `object` | Provider credential configuration |
| `Properties` | `object` | Provider-specific values such as `userId`, `calendarId`, and `folderId` |

#### Google Credentials and Properties

Google credentials use `Type: "GoogleOAuthCredential"`:

| Name | Type | Description |
|---|---|---|
| `ClientId` | `string` | OAuth client id |
| `ClientSecret` | `string` | OAuth client secret |

Google endpoint properties:

| Name | Type | Description |
|---|---|---|
| `userId` | `string` | Google account email; must match the authenticated account |
| `calendarId` | `string` | Calendar id used when synchronizing calendar events; defaults to `primary` |

#### Microsoft Credentials and Properties

Microsoft credentials use `Type: "MicrosoftClientCredential"`:

| Name | Type | Description |
|---|---|---|
| `TenantId` | `string` | Entra tenant id |
| `ClientId` | `string` | Application client id |
| `ClientSecret` | `string` | Client secret, as an alternative to a certificate |
| `CertificatePath` | `string` | PFX/PKCS#12 certificate path, as an alternative to a client secret |
| `CertificatePassword` | `string` | Certificate password when required |

Microsoft endpoint properties:

| Name | Type | Description |
|---|---|---|
| `userId` | `string` | Mailbox owner to access |
| `folderId` | `string` | Contact folder id used when synchronizing contacts; defaults to the mailbox contacts collection |
| `calendarId` | `string` | Calendar id used when synchronizing calendar events; defaults to the mailbox calendar collection |

## Canonical Mapping

Kagami maps provider resources to canonical contacts and events before synchronization. Canonical models allow matching, change detection, filtering, and conflict handling to work consistently across Microsoft Graph, Google, and local import/export endpoints.

### Contacts

Canonical contacts currently include:

- names
- email addresses
- phone numbers
- postal addresses
- organization
- job title
- notes
- birthday
- categories and labels
- contact photos

Microsoft Graph categories and Google contact-group memberships are mapped to canonical categories. The effective contact name used by filters and matching is `DisplayName` when present and `Organization` otherwise.

### Events

Canonical events currently include:

- title
- description
- start and end date-time
- location
- organizer
- attendees
- recurrence
- iCal UID

Synced events are created as attendee-free, mailbox-owned copies. Attendees are ignored during synchronization updates to avoid sending stale invitations and provider-specific mailbox mismatches, while event exports retain organizer and attendee data.

Google Calendar creates regular events with `events.insert` and synchronizes regular calendar events only; special event types such as birthdays are skipped. Microsoft Graph maps timestamps through `dateTimeTimeZone` payloads and uses event delta tokens for incremental polling.

## Logging

Kagami logs the major phases and outcomes of each operation: configuration and endpoint selection, provider reads and writes, synchronization planning, creates, updates, deletes, skipped unchanged items, cursor changes, and failures. Synchronization actions are also recorded in the SQLite operation log so that completed and failed item actions can be diagnosed after a run.

Normal logging uses the .NET logging configuration embedded in the application. The default settings keep application messages at `Information` and suppress noisy HTTP client and Polly messages.

### Verbose Logging

Use `--verbose` to load more detailed logging for a run:

```bash
kagami contacts sync --from Microsoft --to Google --verbose
```

The verbose settings are equivalent to:

```json
{
  "Logging": {
    "LogLevel": {
      "Summerdawn.Kagami": "Debug",
      "System.Net.Http.HttpClient": "Information"
    }
  }
}
```

## Error Handling

Each synchronization pass uses a conservative fault policy designed to avoid silent gaps in synchronization coverage.

### Transient Provider Failures

When a connector returns a rate-limit response (HTTP 429) or a server error (5xx), Kagami aborts the entire run immediately:

- no further items are processed
- the operation is marked faulted
- cursors are not advanced

The next run retries from the same cursor position. Items already written before the failure are recognized from link state and matching version hashes, so replay does not normally produce duplicate writes.

### Permanent Per-Item Failures

For failures other than HTTP 429 and 5xx, Kagami logs the affected item, records an `error` entry in the operation log, and continues processing the remaining items. The pass emits a summary warning and advances its cursors, so the bad item is not retried on every subsequent run.

After correcting the underlying data or configuration, use `--full` to reprocess all items.

### Cursor Advancement

| Run outcome | Cursor advanced? |
|---|---|
| All items succeeded | Yes |
| Transient provider failure (429 / 5xx) | No; the run aborts |
| Permanent per-item failure | Yes; the pass completes with a warning |
| `--what-if` run | No; no writes are performed |

Link-state updates written before a transient failure are not rolled back. On replay, items with a matching version hash are skipped automatically.

## License

This project is licensed under the MIT License.
