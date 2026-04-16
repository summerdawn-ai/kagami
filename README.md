# Kagami

Kagami is a polling-first contact synchronization daemon and library with internal SQLite state.

It currently supports:

- **Microsoft Graph** via confidential client / app-only auth
- **Google People API** via OAuth 2.0 user sign-in with cached refresh tokens

At the moment, the concrete built-in provider connectors are focused on **contacts**. Calendar models exist in the codebase, but concrete Google/Exchange calendar connectors are intentionally not included yet.

## Overview

Kagami synchronizes items between two configured endpoints, stores cursors and link state in SQLite, and runs either once or continuously on a polling schedule.

For contacts, it currently supports:

- Google People API contacts
- Microsoft Graph / Exchange Online contacts
- Contact categories / labels
  - Graph `categories`
  - Google contact-group memberships / labels
- Interactive CLI commands to list, export, and sync contacts between configured endpoints
- OData-style in-memory contact filters for list, export, and sync operations
- `--force` mode to re-evaluate all in-scope contacts even when no versions changed
- Cached Google OAuth tokens for repeat runs after the initial interactive sign-in
- `--what-if` mode that logs each planned create / update / delete without writing changes

## Getting Started

### Prerequisites

- .NET SDK 10 or later
- A Google Cloud project with the People API enabled
- A Google OAuth client configured for the loopback callback `http://localhost:4189/`
- A Microsoft Entra app registration with Microsoft Graph application permissions

### Build from source

```bash
dotnet build
```

### Run tests

```bash
dotnet test
```

### Run the CLI

```bash
dotnet run --project src/Summerdawn.Kagami.Cli -- --help
dotnet run --project src/Summerdawn.Kagami.Cli -- contacts --help
dotnet run --project src/Summerdawn.Kagami.Cli -- jobs --help
```

## CLI Usage

Kagami exposes two top-level command groups:

```text
# Scheduled-job management
kagami jobs list
kagami jobs run [--job=<key>] [--once] [--all] [--what-if]
kagami jobs reset --job=<key>
kagami jobs reset --all

# Interactive contact operations
kagami contacts list   --from=<endpoint> [--filter=<expr>] [--all]
kagami contacts export --from=<endpoint> --to=<dir> [--filter=<expr>]
kagami contacts import --from=<dir> --to=<endpoint> [--prune] [--filter=<expr>] [--what-if]
kagami contacts sync   --from=<endpoint> --to=<endpoint> [--bidirectional]
                       [--prune] [--on-conflict=last-write-wins|source-wins|dest-wins|skip]
                       [--what-if] [--filter=<expr>] [--force]
```

### Jobs commands

#### `kagami jobs list`

Print all configured jobs with their status, endpoints, and schedule:

```bash
kagami jobs list --settings appsettings.json
```

#### `kagami jobs run`

Run continuously by default, either for all enabled jobs or for a single named job. Use `--once` to execute the targeted jobs immediately one time and exit; this bypasses the configured schedule for that invocation. The optional `--all` flag is accepted for compatibility but is not required.

```bash
# Run all enabled jobs continuously (default)
kagami jobs run --settings appsettings.json

# Run a single job continuously
kagami jobs run --job contacts-sync --settings appsettings.json

# Run all enabled jobs once, immediately, and exit
kagami jobs run --once --settings appsettings.json

# Run a single job once with what-if
kagami jobs run --job contacts-sync --once --what-if --settings appsettings.json

# Compatibility form; equivalent to the default continuous mode
kagami jobs run --all --settings appsettings.json
```

#### `kagami jobs reset`

Clear cursors and link state for one or all jobs:

```bash
# Reset a single job
kagami jobs reset --job contacts-sync --settings appsettings.json

# Reset all configured jobs
kagami jobs reset --all --settings appsettings.json
```

### Contacts commands

The `--from` and `--to` options reference **endpoint names** as defined in your
settings file `Endpoints` section (e.g. `Microsoft`, `Google`).

#### `kagami contacts list`

Fetch and display contacts from a configured endpoint.
By default, `kagami contacts list` returns up to 100 matching contacts; use `--all` to fetch the full result set:

```bash
kagami contacts list --from Microsoft --settings appsettings.json

# Fetch every matching contact
kagami contacts list --from Microsoft --all --settings appsettings.json
```

Optionally restrict scope with an OData-style filter (see [Filter expressions](#filter-expressions)):

```bash
kagami contacts list --from Microsoft --filter "startswith(name,'A')" --settings appsettings.json
```

#### `kagami contacts export`

Export contacts as one JSON file per contact into a local directory.
Existing `*.json` files in the destination are deleted before writing.
Files are named from the effective contact name (display name, otherwise organization), sanitized to a filesystem-safe slug such as `alice_smith.json`; a numeric suffix is added for duplicate names. If no usable name exists, Kagami falls back to the contact ID.

```bash
kagami contacts export --from Microsoft --to ./export --settings appsettings.json
```

#### `kagami contacts import`

Import contacts from local JSON files in a directory into a configured endpoint.

```bash
# Import all contacts from a local directory
kagami contacts import --from ./export --to Google --settings appsettings.json

# Import with prune: remove destination contacts not present in the import set
kagami contacts import --from ./export --to Google --prune --settings appsettings.json

# Dry run
kagami contacts import --from ./export --to Google --what-if --settings appsettings.json
```

##### `--prune`

When specified, deletes any contact on the destination that did not appear in the import set. Combines naturally with `--from` pointing to an exported directory to achieve a mirror-replace workflow.

#### `kagami contacts sync`

Synchronize contacts between two configured endpoints.

```bash
# Forward sync (default: source to destination)
kagami contacts sync --from Microsoft --to Google --settings appsettings.json

# Bidirectional sync
kagami contacts sync --from Microsoft --to Google --bidirectional --settings appsettings.json

# One-directional with prune (mirror mode)
kagami contacts sync --from Microsoft --to Google --prune --settings appsettings.json

# Dry run: log planned actions without writing anything
kagami contacts sync --from Microsoft --to Google --what-if --settings appsettings.json

# Force: re-evaluate all in-scope contacts even if unchanged
kagami contacts sync --from Microsoft --to Google --force --settings appsettings.json

# Filter: only synchronize contacts whose effective name starts with 'A'
kagami contacts sync --from Microsoft --to Google --filter "startswith(name,'A')" --settings appsettings.json
```

##### `--bidirectional`

Sync in both directions. By default (without this flag), changes flow only from the source endpoint (`--from`) to the destination endpoint (`--to`).

##### `--prune`

Delete contacts on the destination that no longer exist on the source. Enables a mirror-replace workflow.

##### `--on-conflict`

Conflict resolution policy for items changed on both sides. Available values: `last-write-wins` (default), `source-wins`, `dest-wins`, `skip`.

##### `--force`

Re-syncs all in-scope contacts even if their version/hash has not changed since the last sync run.
Performs a full enumeration (ignores the stored cursor) and re-applies all contacts through the
normal conflict-resolution rules (last-write-wins by default). Useful after extending the sync
algorithm with new fields where a regular incremental run would be a no-op.

##### `--filter`

Accepts an OData-style expression that is applied in memory to fetched contacts.
For sync operations, only matching contacts are planned or written; out-of-scope contacts are left untouched on both sides.

#### Filter expressions

| Expression | Meaning |
|---|---|
| `startswith(name,'A')` | Effective contact name starts with `A` (case-insensitive) |
| `endswith(name,'son')` | Effective contact name ends with `son` (case-insensitive) |
| `contains(name,'Smith')` | Effective contact name contains `Smith` (case-insensitive) |
| `name eq 'Alice'` | Effective contact name is exactly `Alice` (case-insensitive) |

`name` maps to the effective contact name: `DisplayName` when present, otherwise `Organization`.


## Configuration

Kagami reads configuration from the `Kagami` section of a settings JSON file.

Example:

```json
{
  "Kagami": {
    "Host": {
      "MaxConcurrentJobs": 1,
      "SchedulerIntervalSeconds": 30
    },
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
      }
    },
    "Jobs": {
      "contacts-sync": {
        "Enabled": true,
        "EntityType": "contact",
        "Source": "googleContacts",
        "Destination": "exchangeContacts",
        "SyncMode": "Bidirectional",
        "DeletePolicy": "Mirror",
        "ConflictPolicy": "LastWriteWins",
        "Schedule": "PT15M"
      }
    }
  }
}
```

## Config Structure

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
- Access and refresh tokens are cached under `%LOCALAPPDATA%\Summerdawn.ai\Kagami\tokens`
- Kagami requests the Google contacts scope: `https://www.googleapis.com/auth/contacts`
- The built-in Google contacts connector currently uses end-user OAuth; service-account and domain-wide-delegation auth are not supported

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

- reads/writes Microsoft Graph contacts
- maps Outlook categories to canonical contact categories / labels

### `Jobs`

Each job connects exactly two endpoints.

Important values:

- `EntityType`: use `contact`
- `SyncMode`: `Bidirectional`, `Forward`, or `Reverse`
- `DeletePolicy`: use `Mirror` if deletes should propagate
- `ConflictPolicy`: currently `LastWriteWins`, `SourceWins`, `DestinationWins`, or `Skip`
- `Schedule`: interval string such as `PT15M`

### State database

Kagami stores its SQLite state database at `%LOCALAPPDATA%\Summerdawn.ai\Kagami\kagami-state.db` alongside the token cache. The directory is created automatically on first run. The database path is not configurable.

## Authentication Setup

### Microsoft Graph / Exchange Online

Minimal unattended setup:

1. Create a **single-tenant** app registration in your Entra tenant
2. Grant the required **application** permissions
3. Grant admin consent
4. Configure Kagami with either:
   - a client secret, or
   - preferably a certificate

Required Graph permission for write sync:

- `Contacts.ReadWrite` (Application)

Kagami uses app-only access against:

- `/users/{userId}/contacts`
- or `/users/{userId}/contactFolders/{folderId}/contacts`

### Google Workspace / People API

Minimal setup:

1. Create one Google Cloud project
2. Enable the People API
3. Create an OAuth 2.0 client for a desktop or installed application
4. Configure the loopback callback `http://localhost:4189/`
5. Configure Kagami with `clientId` and `clientSecret`
6. Run a Google-backed Kagami command once and complete the browser sign-in flow

Kagami uses:

- Google OAuth 2.0 authorization-code flow with a local loopback callback
- cached access and refresh tokens for subsequent runs
- the People API contacts scope
- `people.connections.list` sync tokens for incremental polling

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

## Architecture

Kagami stores three main kinds of local state in SQLite:

- endpoint cursors / sync tokens
- link state between side A and side B item IDs
- operation logs and job leases

High-level flow:

```text
[Endpoint A] ⇄ [Kagami planner/executor + SQLite state] ⇄ [Endpoint B]
```

## Repository Structure

- `src/Summerdawn.Kagami/`: core library, connector implementations, planner, executor, SQLite state
- `src/Summerdawn.Kagami.Cli/`: command-line host
- `tests/Summerdawn.Kagami.Tests/`: unit and integration-style tests

## Current Limitations

- Built-in concrete provider connectors are currently contact-focused
- Calendar provider connectors are not implemented yet
- What-if mode logs planned operations but intentionally does not update cursors or link state

## Development

This repository uses standard .NET commands:

```bash
dotnet build
dotnet test
```

## License

This project is intended to be MIT-licensed.
