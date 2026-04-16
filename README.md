# Kagami

Kagami is a CLI tool and daemon to import, export and synchronize contacts.

It currently ships with built-in connectors for:

- **Microsoft Graph** contacts via confidential client / app-only auth
- **Google People API** contacts via OAuth 2.0 user sign-in with cached refresh tokens

## Overview

Kagami can be used as a library or as a command-line tool. It synchronizes items between configured endpoints, stores cursors and link state in SQLite, and can run once or continuously on a polling schedule.

### Features

- **Polling-first sync engine** with persistent cursor and link-state tracking
- **Bidirectional or one-way sync** with configurable conflict and delete policies
- **Interactive CLI** for listing, exporting, importing, and synchronizing contacts
- **Built-in provider connectors** for Google People API and Microsoft Graph contacts
- **SQLite-backed local state** for cursors, link state, leases, and operation logs
- **Trim and AOT-friendly packaging** for the shipped project and tool
- **Open source** under the MIT License

## Getting Started

Detailed installation, CLI usage, library registration, configuration structure, and provider setup live in the project README:

- [Summerdawn.Kagami project README](src/Summerdawn.Kagami/README.md)

## Repository Structure

The repository is structured as follows:

- [.github](.github/): GitHub Actions workflows
- [src/Summerdawn.Kagami](src/Summerdawn.Kagami/): library and CLI project, built-in connectors, planner, executor, and SQLite persistence
- [tests/Summerdawn.Kagami.Tests](tests/Summerdawn.Kagami.Tests/): unit and integration-style tests

## Development

### Prerequisites

- .NET SDK 10.0 or later
- A supported OS for regular development; the trimmed single-file validation build targets `win-x64`

### Building from Source

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

### AOT Compatibility Check

```bash
dotnet build src/Summerdawn.Kagami -c Release -r win-x64 --self-contained -p:DebugSymbols=false -p:GenerateDocumentationFile=false -p:IncludeNativeLibrariesForSelfExtract=true -p:StaticWebAssetsEnabled=false -p:IsTransformWebConfigDisabled=true -p:PublishTrimmed=true -p:PublishSingleFile=true -o publish -p:NoWarn=IDE0005
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

## Security

See [SECURITY.md](SECURITY.md) for security reporting guidance.

## License

This project is licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.
