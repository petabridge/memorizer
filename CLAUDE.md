# Memorizer Project Guidelines

## NuGet Package Management

This project uses Central Package Management (CPM) via `Directory.Packages.props`.

- **Never use `VersionOverride` attributes** in `.csproj` files
- All package versions must be defined in `Directory.Packages.props`
- When adding new packages, add the `<PackageVersion>` entry to `Directory.Packages.props` first
- Project files should only contain `<PackageReference Include="PackageName" />` without version attributes
