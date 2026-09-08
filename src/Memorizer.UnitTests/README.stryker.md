# Mutation testing (Stryker.NET)

Run from **this directory** (the unit-test project):

```bash
cd src/Memorizer.UnitTests
dotnet tool restore
dotnet stryker
```

Why here and not the repo root: from the root, Stryker auto-discovers
`Memorizer.slnx` and switches to **solution mode**, which ignores the `mutate`
and test-project scoping in `stryker-config.json` and tries to mutate the whole
codebase against the Docker-bound integration suite (thousands of mutants, very
slow). Running from the unit-test project keeps it single-project and unit-only.

Scope is deliberately the pure, fast-to-test core (value objects, entity-id
parsing, search-quality metrics). Storage/actors/controllers need Postgres +
Ollama and are validated by the integration suite instead.
