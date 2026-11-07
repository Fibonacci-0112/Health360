# Health360 in GitHub Codespaces

The Dev Container uses Ubuntu 26.04, .NET SDK 10.0.403, and Docker-in-Docker.
`postCreateCommand` installs Supabase CLI **2.122.0** (matching GitHub Actions CI) and restores the core solution.
It does **not** start Supabase, PowerSync, or any other Docker services.

From the repository root, start services only when backend or sync work needs them:

```bash
bash tools/codespaces/start-local-services.sh
```

A Codespaces-only Compose override disables PowerSync automatic restarts when the Docker daemon restarts. The script starts the reduced Supabase stack used by CI (including Auth, REST, PostgreSQL, and Storage), reads the **local** JWT secret without printing it, and launches PowerSync with Docker Compose. Supabase API is on port 54321, Mailpit on 54324, and PowerSync on 8080. The database is on 54322 inside the Codespace.

When finished:

```bash
bash tools/codespaces/stop-local-services.sh
```

This stops services without deleting their persistent volumes. Rebuilds or deletion of the Codespace may affect local data; never use local data as a production backup.

To reinstall the pinned CLI without starting services:

```bash
bash tools/codespaces/install-supabase-cli.sh
```

Check versions and run core tests:

```bash
supabase --version  # 2.122.0
dotnet --version    # 10.0.403
dotnet test --solution Health360.Core.slnf
```

CI continues to run its own Supabase and PowerSync jobs. The standalone `tools/cloud/setup.sh` remains intended for Claude Code's Docker-free cloud environment, not Codespaces.
