# Kagami

Kagami is a CLI tool to import, export, and synchronize contacts and calendar events.

## Overview

Kagami can be used as a library or as a command-line tool. It synchronizes items between configured endpoints, stores cursors and link state in SQLite, and can run once or continuously at a user-specified interval.

### Features

- **Microsoft Graph** contacts and calendar events via confidential client / app-only auth
- **Google People and Calendar API** for contacts and calendar events via OAuth 2.0 user sign-in with cached refresh tokens
- **SQLite-backed local state** for cursors, link state, leases, and operation logs
- **Polling-first sync engine** with persistent cursor and link-state tracking
- **Bidirectional or one-way sync** with configurable conflict and delete policies
- **Interval mode** to repeat the sync in-process with a fixed delay between runs
- **Interactive CLI** for listing, exporting, importing, and synchronizing contacts and calendar events
- **Trim and AOT-friendly packaging** for the shipped project and tool
- **Open source** under the MIT License

## Documentation

Detailed installation, CLI usage, library registration, configuration structure, and provider setup are documented in the [Summerdawn.Kagami project README](src/Summerdawn.Kagami/README.md).

## Architecture

Kagami separates provider connectors from the synchronization engine. Connectors normalize contacts and events into canonical models, while the engine plans and executes changes using persisted cursors, link state, leases, and operation logs.

### Current Limitations

- **Event attendees are not synchronized**: Kagami synchronizes calendar event details but cannot synchronize attendee lists.
- **Microsoft authentication is non-delegated only**: Microsoft connectors use application permissions with client credentials; delegated user login is not supported.
- **Google authentication needs callback**: Google connectors use OAuth with a local callback during login.

## Repository Structure

- [.github](.github/) — GitHub Actions workflows and repository instructions
- [src/Summerdawn.Kagami](src/Summerdawn.Kagami/) — library and CLI project, built-in connectors, planner, executor, and SQLite persistence
- [tests/Summerdawn.Kagami.Tests](tests/Summerdawn.Kagami.Tests/) — unit and integration-style tests

## Development

### Prerequisites

- .NET SDK 10.0 or later
- A supported OS for regular development; the trimmed single-file validation build targets `win-x64`

### Getting the Code

Clone the repository and restore dependencies:

```bash
git clone https://github.com/summerdawn-ai/kagami.git

cd kagami
dotnet restore
```

### Building from Source

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

For installation, configuration, and CLI usage, see the [Summerdawn.Kagami project README](src/Summerdawn.Kagami/README.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

## Security

See [SECURITY.md](SECURITY.md) for security reporting guidance.

## License

This project is licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.
