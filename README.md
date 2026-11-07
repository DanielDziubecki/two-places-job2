# two-places-job2

A small C# (.NET 10) worker agent for job 2 of the MEGA.dev "two places" challenge.
It connects to the job's MCP link (Streamable HTTP), loads the `voice-to-memo` skill
(served on that link, with a bundled copy in `skills/` as a fallback), and runs an OpenRouter
tool-calling loop that only sees that job's tools. The main agent fetches the result.

## Endpoints

| Method | Path      | Auth                     | What it does                                   |
|--------|-----------|--------------------------|------------------------------------------------|
| GET    | `/health` | none                     | Liveness check                                 |
| POST   | `/run`    | `X-Result-Secret` header | Starts the job (`?force=1` re-runs after done) |
| GET    | `/result` | `X-Result-Secret` header | Status, submitted notes, submit reply (receipt), token usage and a trace of every step |

## Environment variables (set them in Railway, never in the repo)

| Name                | Required | Value                                       |
|---------------------|----------|---------------------------------------------|
| `JOB_MCP_URL`       | yes      | Job 2's MCP link (it is the key: keep it secret) |
| `CLIP_URL`          | yes      | Job 2's clip URL                            |
| `OPENROUTER_API_KEY` | yes     | Your OpenRouter API key                     |
| `RESULT_SECRET`     | yes      | 16+ random characters; the main agent sends it in `X-Result-Secret` |
| `OPENROUTER_MODEL`  | no       | Defaults to `anthropic/claude-sonnet-5.5`   |
| `RUN_ON_START`      | no       | `1` starts the job as soon as the service boots |

Railway sets `PORT`; the app listens on it (default 8080).

## Deploy on Railway

1. New Project → Deploy from GitHub repo → pick this repo. Railway builds the `Dockerfile`.
2. Add the variables above under the service's Variables tab.
3. Settings → Networking → Generate Domain.
4. Check `https://<domain>/health` returns `{"ok":true}`.

## Run locally

```bash
dotnet run
```
