# Kagami

Kagami is a polling-first contact synchronization daemon and library with internal SQLite state.

It is designed for unattended execution with app/service authentication:

- **Microsoft Graph** via confidential client / app-only auth
- **Google People API** via service account auth, optionally with domain-wide delegation

At the moment, the concrete built-in provider connectors are focused on **contacts**. Calendar models exist in the codebase, but concrete Google/Exchange calendar connectors are intentionally not included yet.

## Overview

Kagami synchronizes items between two configured endpoints, stores cursors and link state in SQLite, and runs either once or continuously on a polling schedule.

For contacts, it currently supports:

- Google People API contacts
- Microsoft Graph / Exchange Online contacts
- Contact categories / labels
  - Graph `categories`
  - Google contact-group memberships / labels
- Headless unattended auth suitable for local/server daemon use
- `--what-if` mode that logs each planned create / update / delete without writing changes

## Getting Started

### Prerequisites

- .NET SDK 10 or later
- A Google Cloud project with the People API enabled
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
kagami contacts list   --from=<endpoint> [--filter=<expr>]
kagami contacts export --from=<endpoint> --to=<dir> [--filter=<expr>]
kagami contacts sync   --from=<endpoint> --to=<endpoint> [--mode=bidi|a-to-b|b-to-a]
                       [--what-if] [--filter=<expr>] [--force]
```

### Jobs commands

#### `kagami jobs list`

Print all configured jobs with their status, endpoints, and schedule:

```bash
kagami jobs list --config appsettings.json
```

#### `kagami jobs run`

Run all enabled jobs once (default), a single named job, or continuously:

```bash
# Run all due jobs once and exit (default)
kagami jobs run --config appsettings.json

# Run a single job with what-if
kagami jobs run --job contacts-sync --what-if --config appsettings.json

# Run continuously, polling on configured schedules
kagami jobs run --all --config appsettings.json
```

#### `kagami jobs reset`

Clear cursors and link state for one or all jobs:

```bash
# Reset a single job
kagami jobs reset --job contacts-sync --config appsettings.json

# Reset all configured jobs
kagami jobs reset --all --config appsettings.json
```

### Contacts commands

The `--from` and `--to` options reference **endpoint names** as defined in your
`appsettings.json` `Endpoints` section (e.g. `Microsoft`, `Google`).

#### `kagami contacts list`

Fetch and display all contacts from a configured endpoint:

```bash
kagami contacts list --from Microsoft --config appsettings.json
```

Optionally restrict scope with an OData-style filter (see [Filter expressions](#filter-expressions)):

```bash
kagami contacts list --from Microsoft --filter "startswith(name,'A')" --config appsettings.json
```

#### `kagami contacts export`

Export contacts as one JSON file per contact into a local directory.
Existing `*.json` files in the destination are deleted before writing.
Files are named `lastname_firstname[_N].json`; a numeric suffix is added for duplicate names.

```bash
kagami contacts export --from Microsoft --to ./export --config appsettings.json
```

#### `kagami contacts sync`

Synchronize contacts between two configured endpoints.

```bash
# Bidirectional sync (default)
kagami contacts sync --from Microsoft --to Google --config appsettings.json

# One-directional
kagami contacts sync --from Microsoft --to Google --mode a-to-b --config appsettings.json

# Dry run: log planned actions without writing anything
kagami contacts sync --from Microsoft --to Google --what-if --config appsettings.json

# Force: re-evaluate all in-scope contacts even if unchanged
kagami contacts sync --from Microsoft --to Google --force --config appsettings.json

# Filter: only synchronize contacts whose display name starts with 'A'
kagami contacts sync --from Microsoft --to Google --filter "startswith(name,'A')" --config appsettings.json
```

##### `--what-if`

Performs planning only. Logs each planned create, update, and delete without writing to either endpoint or updating sync state.

##### `--force`

Re-syncs all in-scope contacts even if their version/hash has not changed since the last sync run.
Performs a full enumeration (ignores the stored cursor) and re-applies all contacts through the
normal conflict-resolution rules (last-write-wins by default). Useful after extending the sync
algorithm with new fields where a regular incremental run would be a no-op.

##### `--filter`

Accepts an OData-style expression that restricts the scope of the command on **both** sides.
Contacts not matching the filter are completely unaffected — they are neither read from the
source beyond what the provider returns, nor written to the destination.

#### Filter expressions

| Expression | Meaning |
|---|---|
| `startswith(name,'A')` | Display name starts with `A` (case-insensitive) |
| `endswith(name,'son')` | Display name ends with `son` (case-insensitive) |
| `contains(name,'Smith')` | Display name contains `Smith` (case-insensitive) |
| `name eq 'Alice'` | Display name is exactly `Alice` (case-insensitive) |


## Configuration

Kagami reads configuration from the `Kagami` section of `appsettings.json`.

Example:

```json
{
  "Kagami": {
    "Persistence": {
      "DatabasePath": "/var/lib/kagami/state.db"
    },
    "Host": {
      "MaxConcurrentJobs": 1,
      "SchedulerIntervalSeconds": 30
    },
    "Credentials": {
      "googleWorkspace": {
        "Type": "google-service-account",
        "Properties": {
          "jsonPath": "/etc/kagami/google-service-account.json",
          "impersonatedUser": "person@summerdawn.ai"
        }
      },
      "graphApp": {
        "Type": "graph-client-credentials",
        "Properties": {
          "tenantId": "00000000-0000-0000-0000-000000000000",
          "clientId": "11111111-1111-1111-1111-111111111111",
          "clientSecret": "replace-me"
        }
      }
    },
    "Endpoints": {
      "googleContacts": {
        "Type": "google-contacts",
        "Credential": "googleWorkspace",
        "Properties": {}
      },
      "exchangeContacts": {
        "Type": "graph-contacts",
        "Credential": "graphApp",
        "Properties": {
          "userId": "person@summerdawn.ai"
        }
      }
    },
    "Jobs": {
      "contacts-sync": {
        "Enabled": true,
        "EntityType": "contact",
        "EndpointA": "googleContacts",
        "EndpointB": "exchangeContacts",
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

### `Credentials`

Named reusable credential definitions.

#### Google service account

Use:

- `Type = "google-service-account"`

Properties:

- `jsonPath`: path to the service-account JSON key file
- `json`: alternative inline JSON string if you do not want to use a file
- `impersonatedUser`: optional Workspace user email for domain-wide delegation

Notes:

- Kagami requests the Google contacts scope: `https://www.googleapis.com/auth/contacts`
- If you want Kagami to work against a Workspace user's contacts, use domain-wide delegation and set `impersonatedUser`

#### Microsoft Graph confidential client

Use:

- `Type = "graph-client-credentials"`

Properties:

- `tenantId`: Entra tenant ID
- `clientId`: app registration client ID
- `clientSecret`: client secret for MVP setups
- `certificatePath`: optional PFX/PKCS#12 certificate path for long-term unattended use
- `certificatePassword`: optional certificate password

Use either:

- `clientSecret`, or
- `certificatePath` (+ `certificatePassword` if needed)

### `Endpoints`

Named endpoint definitions that bind a connector type to a credential.

#### `google-contacts`

Properties:

- none required

Behavior:

- reads/writes Google contacts through the People API
- maps Google contact-group memberships to canonical contact categories / labels
- creates missing Google contact groups when needed for synchronized labels

#### `graph-contacts`

Properties:

- `userId`: required; the mailbox owner to access, typically a user principal name or user ID
- `folderId`: optional contact folder ID; if omitted, Kagami uses the default contacts collection

Behavior:

- reads/writes Microsoft Graph contacts
- maps Outlook categories to canonical contact categories / labels

### `Jobs`

Each job connects exactly two endpoints.

Important values:

- `EntityType`: use `contact`
- `SyncMode`: `Bidirectional`, `AToB`, or `BToA`
- `DeletePolicy`: use `Mirror` if deletes should propagate
- `ConflictPolicy`: currently `LastWriteWins`, `SideAWins`, `SideBWins`, or `Skip`
- `Schedule`: interval string such as `PT15M`

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

Minimal unattended setup:

1. Create one Google Cloud project
2. Create one service account
3. Enable the People API
4. If you need to operate on a Workspace user's contacts, configure **domain-wide delegation**
5. Configure Kagami with the service-account JSON and, when applicable, `impersonatedUser`

Kagami uses:

- Google service-account auth
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
