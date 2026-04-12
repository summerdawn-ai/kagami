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
```

## CLI Usage

Kagami exposes three commands:

```text
kagami run
kagami once
kagami reset <job-key>
```

### Run once

Execute all due jobs once:

```bash
dotnet run --project src/Summerdawn.Kagami.Cli -- once --config appsettings.json
```

Run a single job:

```bash
dotnet run --project src/Summerdawn.Kagami.Cli -- once --config appsettings.json --job contacts-sync
```

### What-if mode

`--what-if` performs planning only. It does **not** write remote changes or update Kagami state. Instead, it logs each planned contact create, update, and delete:

```bash
dotnet run --project src/Summerdawn.Kagami.Cli -- once --config appsettings.json --what-if
```

### Continuous mode

Run continuously on the configured polling schedules:

```bash
dotnet run --project src/Summerdawn.Kagami.Cli -- run --config appsettings.json
```

### Reset state

Clear cursors and link state for a single job:

```bash
dotnet run --project src/Summerdawn.Kagami.Cli -- reset contacts-sync --config appsettings.json
```

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

This project is licensed under the MIT License. See `LICENSE.md` when added to the repository.
