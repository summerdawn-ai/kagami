# Summerdawn.Kagami

Kagami is a polling-first contact synchronization daemon and library with internal SQLite state.

## Overview

Kagami can be used as a library or as a command-line tool. It synchronizes items between configured endpoints, stores cursors and link state in SQLite, and can run either once or continuously on a polling schedule.

At the moment, the built-in provider connectors are focused on **contacts**. Calendar models exist in the codebase, but concrete Google and Microsoft calendar connectors are intentionally not included yet.

### Features

- Google People API contacts
- Microsoft Graph / Exchange Online contacts
- Contact categories / labels
  - Graph `categories`
  - Google contact-group memberships / labels
- Interactive CLI commands to list, export, import, and sync contacts
- OData-style in-memory contact filters for list, export, and sync operations
- `--force` mode to re-evaluate all in-scope contacts even when no versions changed
- `--what-if` mode that logs planned create / update / delete operations without writing changes
- Cached Google OAuth tokens for repeat runs after the initial interactive sign-in

## Getting Started

The fastest way to get started is to install Kagami as a .NET tool, create a settings file, and run one of the CLI commands:

```bash
dotnet tool install --global Summerdawn.Kagami
kagami --help
kagami contacts --help
kagami jobs --help
```

If you are developing from source instead, run:

```bash
dotnet run --project src/Summerdawn.Kagami -- --help
dotnet run --project src/Summerdawn.Kagami -- contacts --help
dotnet run --project src/Summerdawn.Kagami -- jobs --help
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

This registers the sync engine, persistence services, `ContactsService`, and a named `HttpClient` and keyed `IConnector` singleton for each configured endpoint. Connectors are resolved by endpoint name via `Func<string, IConnector>`.

### CLI commands

Kagami exposes two top-level command groups:

```text
# Scheduled-job management
kagami jobs list
kagami jobs run [--job=<key>] [--once] [--all] [--what-if]
kagami jobs reset --job=<key>
kagami jobs reset --all
kagami jobs unlock

# Interactive contact operations
kagami contacts list   --from=<endpoint> [--filter=<expr>] [--all]
kagami contacts export --from=<endpoint> --to=<dir> [--filter=<expr>]
kagami contacts import --from=<dir> --to=<endpoint> [--prune] [--filter=<expr>] [--what-if]
kagami contacts sync   --from=<endpoint> --to=<endpoint> [--bidirectional]
                       [--prune] [--on-conflict=last-write-wins|source-wins|dest-wins|skip]
                       [--what-if] [--filter=<expr>] [--force]
```

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

#### `kagami jobs unlock`

Force-release all stored job locks after an interrupted run:

```bash
kagami jobs unlock --settings appsettings.json
```

#### `kagami contacts list`

Fetch and display contacts from a configured endpoint. By default, `kagami contacts list` returns up to 100 matching contacts; use `--all` to fetch the full result set.

```bash
kagami contacts list --from Microsoft --settings appsettings.json
kagami contacts list --from Microsoft --all --settings appsettings.json
kagami contacts list --from Microsoft --filter "startswith(name,'A')" --settings appsettings.json
```

#### `kagami contacts export`

Export contacts as one JSON file per contact into a local directory. Existing `*.json` files and previously exported photo files in the destination are deleted before writing.

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

When `--prune` is specified, Kagami deletes destination contacts that did not appear in the import set.

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

- `--bidirectional`: sync in both directions; otherwise changes flow from `--from` to `--to`
- `--prune`: delete destination contacts that no longer exist on the source
- `--on-conflict`: `last-write-wins` (default), `source-wins`, `dest-wins`, or `skip`
- `--force`: re-sync all in-scope contacts even if their version/hash has not changed
- `--filter`: apply an OData-style filter in memory before planning or writing changes

### `--force` semantics and filter-scope deletions

#### What `--force` means

Running with `--force` is semantically equivalent to running the job for the first time with an empty state database. The end result — which items exist on each side and are linked — is the same as a first run, **except** that existing links are reused rather than re-created from scratch. `--force` does **not** mean "clobber the destination regardless of conflict policy"; conflict policies still apply in full.

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

### Filter expressions

| Expression | Meaning |
|---|---|
| `startswith(name,'A')` | Effective contact name starts with `A` (case-insensitive) |
| `endswith(name,'son')` | Effective contact name ends with `son` (case-insensitive) |
| `contains(name,'Smith')` | Effective contact name contains `Smith` (case-insensitive) |
| `name eq 'Alice'` | Effective contact name is exactly `Alice` (case-insensitive) |

`name` maps to the effective contact name: `DisplayName` when present, otherwise `Organization`.

## Configuration

Kagami reads configuration from the `Kagami` section of a settings JSON file.

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
- Kagami requests the Google contacts scope `https://www.googleapis.com/auth/contacts`
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

- reads and writes Microsoft Graph contacts
- maps Outlook categories to canonical contact categories / labels

### `Jobs`

Each job connects exactly two endpoints.

Important values:

- `EntityType`: use `contact`
- `SyncMode`: `Bidirectional`, `Forward`, or `Reverse`
- `DeletePolicy`: use `Mirror` if deletes should propagate
- `ConflictPolicy`: `LastWriteWins`, `SourceWins`, `DestinationWins`, or `Skip`
- `Schedule`: interval string such as `PT15M`

## State Database

Kagami stores its SQLite state database at `%LOCALAPPDATA%\Summerdawn.ai\Kagami\kagami-state.db` alongside the token cache. The directory is created automatically on first run. The database path is not configurable.

## Authentication Setup

### Microsoft Graph / Exchange Online

Minimal unattended setup:

1. Create a single-tenant app registration in your Entra tenant
2. Grant the required application permissions
3. Grant admin consent
4. Configure Kagami with either a client secret or, preferably, a certificate

Required Graph permission for write sync:

- `Contacts.ReadWrite` (Application)

Kagami uses app-only access against:

- `/users/{userId}/contacts`
- `/users/{userId}/contactFolders/{folderId}/contacts`

### Google Workspace / People API

Minimal setup:

1. Create a Google Cloud project
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

## Current Limitations

- Built-in concrete provider connectors are currently contact-focused
- Calendar provider connectors are not implemented yet
- What-if mode logs planned operations but intentionally does not update cursors or link state

## Development

```bash
dotnet build
dotnet test
```
