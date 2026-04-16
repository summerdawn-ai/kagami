# Contributing

Thanks for your interest in contributing to Kagami.

## Getting Started

1. Fork the repository and create a topic branch.
2. Make focused changes with tests where applicable.
3. Run the local validation commands before opening a pull request:

```bash
dotnet build
dotnet test
dotnet build src/Summerdawn.Kagami -c Release -r win-x64 --self-contained -p:DebugSymbols=false -p:GenerateDocumentationFile=false -p:IncludeNativeLibrariesForSelfExtract=true -p:StaticWebAssetsEnabled=false -p:IsTransformWebConfigDisabled=true -p:PublishTrimmed=true -p:PublishSingleFile=true -o publish -p:NoWarn=IDE0005
```

## Pull Requests

- Keep pull requests scoped to a single change or feature.
- Update documentation when user-facing behavior, configuration, or packaging changes.
- Preserve trim/AOT compatibility for new JSON serialization paths and configuration binding.

## Code Style

- Follow the repository `.editorconfig` settings.
- Prefer small, explicit changes over broad refactors.
- Do not remove or weaken existing tests to make a change pass.

## Reporting Issues

Use GitHub Issues for bugs, regressions, and feature requests. Include reproduction steps, expected behavior, and relevant logs or configuration snippets.
