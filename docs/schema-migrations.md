# Schema Migrations Guide

This document is for contributors and advanced users who need to change or extend the database schema.

## How Schema Migrations Work

- All migration scripts are stored in `PostgMem/migrations/` and named with an incrementing version prefix, e.g., `001_init.sql`, `002_add_column.sql`.
- On startup, the application runs all unapplied migrations in order.
- Applied migrations are tracked in the `schema_version` table in the database.
- The migration runner (not the SQL script) records each migration after it is successfully applied.

## Adding a New Migration

1. Create a new SQL file in `PostgMem/migrations/` with the next version number, e.g., `002_add_new_table.sql`.
2. Write only the changes needed (e.g., `ALTER TABLE`, `CREATE TABLE`, etc.).
3. Do **not** insert into `schema_version` in the SQL file; the application will handle this.
4. Commit the new migration file to version control.

## Best Practices

- Use descriptive names for migration files after the version prefix.
- Make migrations idempotent if possible (e.g., use `IF NOT EXISTS`).
- Test your migration locally before submitting a PR.
- Never edit a migration that has already been applied to any environment.

## Testing Migrations

- Run the integration tests: `dotnet test PostgMem.IntegrationTests`
- The test suite includes a check that the `schema_version` table is populated correctly.
- You can also manually inspect the `schema_version` table after running the app.

## Troubleshooting

- If a migration fails, fix the migration file and re-run the application.
- If you need to reset your local database, you can drop and recreate it, then rerun the app to reapply all migrations. 