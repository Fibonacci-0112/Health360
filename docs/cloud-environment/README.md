# Cloud environment for Claude Code

Settings for a [Claude Code cloud environment](https://code.claude.com/docs/en/claude-code-on-the-web) that can build and test this repo as planned in [PLAN.md](../PLAN.md).

1. Open the environment's settings (the cloud environment menu in the session's title bar, then **Edit**).
2. **Setup script:** paste [setup.sh](setup.sh).
3. **Environment variables:** paste the non-secret block of [env.example](env.example). Add secrets from its second block only when a task needs them.
4. **Network access:** the default level is enough. Everything installs from npm, Ubuntu apt, `public.ecr.aws` (Supabase images) and Docker Hub (PowerSync). GitHub release downloads are blocked, which is why Deno and the Supabase CLI come from npm.

## What the setup script adds

| Tool | Why |
|---|---|
| Deno 2 | Edge Functions and their tests |
| Supabase CLI 2 | `supabase start`, migrations, `supabase test db` (pgTAP), `gen types` |
| EAS CLI | Expo dev builds and updates (needs `EXPO_TOKEN`) |
| Tauri 2 Linux libraries (WebKitGTK 4.1 and others) | Phase 0 desktop test; turn off with `INSTALL_TAURI_DEPS=0` |
| Docker daemon | Local Supabase and PowerSync |
| `pnpm install` | Runs once the monorepo exists; skipped until then |

Node 22, pnpm 10, Rust, psql and Playwright Chromium are already in the base image.

## Not covered

- **Android emulator and Maestro:** too heavy for the container. Android builds run on EAS.
- **Playwright Firefox and WebKit:** install them through the repo's pinned `@playwright/test` once it exists.
- **Docker after a restart:** if `docker info` fails in a session, run `nohup dockerd >/var/log/dockerd.log 2>&1 &`.
