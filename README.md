# Kagami

Kagami is a synchronization engine for contacts and calendar events between Microsoft Exchange mailboxes and/or Google accounts.

## Overview

Kagami separates provider connectors from a synchronization engine: connectors normalize Exchange and Google contacts and events into canonical models, and the engine plans and executes changes and persists sync state in SQLite. It supports delta updates, filtering, and multiple synchronization modes.

### Features

- **Contacts and Calendar Events**: Synchronizes both contacts and calendar events between Microsoft Exchange mailboxes and Google accounts
- **Delta-Based Incremental Sync**: Reuses provider delta and sync tokens to process only what changed since the last successful run
- **Sync Modes and Policies**: Supports forward, bidirectional, and reverse synchronization, full and forced re-evaluation, configurable conflict policies, and optional delete mirroring
- **Continuous Sync**: Runs indefinitely on a configurable interval for unattended, ongoing synchronization
- **Filtering**: Scope synchronization, import, and export using flexible filter expressions on names, categories, titles, and dates
- **Import and Export**: List, import, and export contacts and events to and from local files independent of synchronization
- **Local, Self-Contained State**: Persists cursors, link state, and an operation log in a local SQLite database, with no external services required
- **Cross-Platform**: Runs on Windows, Linux, and macOS
- **Open Source**: Fully open source under the MIT License

## Getting Started

Detailed installation, command usage, configuration structure, and provider setup are documented in the [Command-line tool README](src/Summerdawn.Kagami/README.md).

## Architecture

Kagami separates provider connectors from the synchronization engine. Connectors normalize contacts and events into canonical models, while the engine plans and executes changes using persisted cursors, link state, leases, and operation logs.

### Dependencies

Kagami targets .NET 10 and is compatible with trimming and AOT compilation. It uses `Microsoft.Data.Sqlite` for local state, which pulls in native, per-platform SQLite binaries.

### Current Limitations

- **Event attendees are not synchronized**: Kagami synchronizes calendar event details but cannot synchronize attendee lists.
- **Microsoft authentication is non-delegated only**: Microsoft connectors use application permissions with client credentials; delegated user login is not supported. As a result, Microsoft personal accounts such as Hotmail are currently not supported.
- **Google authentication needs callback**: Google connectors use OAuth with a local callback during login.

## Repository Structure

- [.github](.github/) — GitHub Actions workflows and repository instructions
- [src/Summerdawn.Kagami](src/Summerdawn.Kagami/) — CLI tool, built-in connectors, planner, executor, and SQLite persistence
- [tests/Summerdawn.Kagami.Tests](tests/Summerdawn.Kagami.Tests/) — unit and integration-style tests

## Versioning and Releases

Kagami is built and released using [the repository's 'release.yml' workflow](.github/workflows/release.yml). It is versioned using [Semantic Versioning](https://semver.org/).

Kagami is published as a NuGet package, and is also published to each GitHub release as a number of standalone, AOT-compiled binaries:

- [GitHub Releases](https://github.com/summerdawn-ai/kagami/releases) including binaries
- [Summerdawn.Kagami on NuGet](https://www.nuget.org/packages/Summerdawn.Kagami)

## Development

### Prerequisites

- .NET SDK 10.0 or later
- A supported OS (Windows, Linux, or macOS)

### Getting the Code

Clone the repository and restore dependencies:

```bash
git clone https://github.com/summerdawn-ai/kagami.git

cd kagami
dotnet restore
```

### Building from Source

The solution can be built using standard .NET CLI commands:

```bash
dotnet build
```

### Running Tests

All tests live under the tests directory and are executed as part of every CI run. To run the full test suite locally:

```bash
dotnet test
```

### Packaging and Publishing

Refer to [the repository's 'release.yml' workflow](.github/workflows/release.yml) for the exact commands used to pack the command-line tool as a NuGet package and publish it as platform-dependent, standalone, AOT-compiled binaries.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

## Security

See [SECURITY.md](SECURITY.md) for security reporting guidance.

## License

This project is licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.
