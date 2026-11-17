# Contributing to OpenTaskbar

Thank you for your interest in contributing to OpenTaskbar!

## Language Policy

- **All issues, pull requests, commit messages, code comments, and documentation must be written in English.**
- Localization files under `Services/Localization/Languages/` naturally contain translated user-facing text, but metadata, keys, and pull request descriptions discussing those translations must still be in English.

## Getting Started

### Prerequisites
- Windows 10 (version 19041 or higher) or Windows 11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- PowerShell 7+ (or Windows PowerShell 5.1)

### Building the Project
```pwsh
dotnet build OpenTaskbar.csproj --warnaserror
```

### Running Tests
The project contains behavior and contract checks in `tests/OpenTaskbar.Tests/`:
```pwsh
dotnet run --project tests/OpenTaskbar.Tests/OpenTaskbar.Tests.csproj
```
Ensure all behavior checks pass before submitting a pull request.

## Development Guidelines

1. **Focused Changes:** Keep pull requests focused on a single topic. Avoid scope creep or bundling unrelated changes.
2. **Preserve Compatibility:** Do not introduce breaking configuration or storage format changes without migration safety.
3. **Coding Standards:**
   - Follow existing architecture: keep business logic, UI, and platform P/Invoke decoupled.
   - Use strict C# 13 / .NET 10 features and idiomatic patterns.
   - Write clear, meaningful comments in English explaining *why*, not just *what*.
   - Never commit credentials, personal data, or build artifacts.

## Submitting a Pull Request

1. Fork the repository and create a branch for your fix or feature.
2. Verify that `dotnet build --warnaserror` succeeds and all tests pass.
3. Fill out the pull request template completely in **English**.
