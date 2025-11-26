# Memorizer Project Guidelines

## NuGet Package Management

This project uses Central Package Management (CPM) via `Directory.Packages.props`.

- **Never use `VersionOverride` attributes** in `.csproj` files
- All package versions must be defined in `Directory.Packages.props`
- When adding new packages, add the `<PackageVersion>` entry to `Directory.Packages.props` first
- Project files should only contain `<PackageReference Include="PackageName" />` without version attributes

## UI Styling and Theme Support

This project supports both light and dark themes. When making CSS or stylesheet changes:

- **Always provide both light and dark mode styles** for any custom CSS
- The theme is controlled via `data-theme` attribute on `<html>` element (`data-theme="dark"` or `data-theme="light"`)
- Use `[data-theme="dark"]` CSS selector to target dark mode styles
- Theme switching is handled by `wwwroot/js/theme-switcher.js`
- Ensure sufficient color contrast in both modes for accessibility
- Example pattern:
  ```css
  /* Light mode (default) */
  .my-class { background-color: #d4edda; color: #155724; }

  /* Dark mode */
  [data-theme="dark"] .my-class { background-color: #1e4620; color: #75d47b; }
  ```
