# Kagami

Kagami supports listing, importing, exporting, and synchronizing contacts and calendar events from Microsoft Graph and Google accounts.

## Overview

Kagami is a .NET CLI tool that synchronizes items between configured endpoints and stores synchronization state in SQLite.

## Documentation

Detailed installation, command usage, configuration structure, and provider setup are documented in the [Summerdawn.Kagami project README](src/Summerdawn.Kagami/README.md).

## Architecture

Kagami separates provider connectors from the synchronization engine. Connectors normalize contacts and events into canonical models, while the engine plans and executes changes using persisted cursors, link state, leases, and operation logs.

### Current Limitations

- **Event attendees are not synchronized**: Kagami synchronizes calendar event details but cannot synchronize attendee lists.
- **Microsoft authentication is non-delegated only**: Microsoft connectors use application permissions with client credentials; delegated user login is not supported.
- **Google authentication needs callback**: Google connectors use OAuth with a local callback during login.

## Repository Structure

- [.github](.github/) — GitHub Actions workflows and repository instructions
- [src/Summerdawn.Kagami](src/Summerdawn.Kagami/) — CLI tool, built-in connectors, planner, executor, and SQLite persistence
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

For installation, configuration, and command usage, see the [Summerdawn.Kagami project README](src/Summerdawn.Kagami/README.md).

## Versioning and Releases

Kagami uses [Semantic Versioning](https://semver.org/). The initial public release is `0.9.0`.

Releases are created with [the `release.yml` workflow](.github/workflows/release.yml). The workflow builds and publishes the `Summerdawn.Kagami` .NET tool package, and attaches standalone, AOT-compiled binaries for Windows, Linux, and macOS to the GitHub release.

- [GitHub Releases](https://github.com/summerdawn-ai/kagami/releases)
- [Summerdawn.Kagami on NuGet](https://www.nuget.org/packages/Summerdawn.Kagami)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

## Security

See [SECURITY.md](SECURITY.md) for security reporting guidance.

## License

This project is licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.
