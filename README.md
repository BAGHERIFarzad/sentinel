# Sentinel — the agent that never forgets an outage

An autonomous SRE incident-response agent whose **entire memory lives in CockroachDB**. When an alert fires, Sentinel recalls every similar incident it has ever handled, reasons over live telemetry with Claude, applies one mitigation at a time, and — when the incident is resolved — writes a postmortem back into its own memory. Every incident makes it smarter.

Sentinel also treats its memory as production infrastructure: a watchdog checks the health of its own CockroachDB cluster through the **ccloud CLI**, and the agent snapshots cluster state via `ccloud` before any mitigation that mutates state. An agent whose memory goes offline doesn't degrade gracefully — it stops. Sentinel is built around that fact.

Built for the **CockroachDB × AWS Hackathon — Build the Future of Agentic Memory**.

## Why memory is the product here

Incident response is exactly the workflow where agent memory is the difference between useful and useless:

- A stateless agent re-derives the same diagnosis every time and repeats mitigations that are *known not to work*.
- Sentinel's recalled postmortems explicitly encode "mitigation that worked" **and** "mitigation that did NOT work" — and its system prompt instructs it to trust that history. In the demo, the agent skips the pod-restart trap because a past postmortem says restarts only masked the pool leak.
- The memory is transactional **and** semantic in one system: the incident state machine, the immutable audit trail, and the vector-indexed postmortems are written by the same database, in the same transactions. No drift between "what happened" and "what it means."

## CockroachDB tools used (2 of 4)

| Tool | How Sentinel uses it |
|---|---|
| **Distributed Vector Indexing** | `memory_items.embedding VECTOR(1024)` under an inline `VECTOR INDEX`. Every alert is embedded (Voyage `voyage-3.5`) and semantically matched against all past postmortems and runbooks (`embedding <-> $1`). Resolved incidents are embedded and written back — the index grows with every outage, with no separate vector store. See `db/schema.sql`, `backend/Sentinel.Core/Memory/MemoryStore.cs`. |
| **ccloud CLI (agent-ready)** | `CcloudWatchdog` polls `ccloud cluster info {id} -o json` every 60s and records the result in `cluster_checks`. Before any `apply_mitigation`, the agent runs the same call again to snapshot cluster state before mutating anything — mirroring the least-privilege "triage agent" pattern CockroachDB's own docs describe (read cluster state, can't touch config). See `backend/Sentinel.Core/Infra/Infra.cs`. |

Also present but not counted toward the "2 of 4" requirement: the incident state machine, audit trail, and postmortem storage all run as ordinary transactional SQL against the same CockroachDB cluster — the same database serves both the vector workload and the operational workload, with no ETL between them. `docs/mcp-setup.md` documents a Managed MCP Server config used for ad hoc schema inspection during development; it isn't called at agent runtime, so it isn't counted as one of the two required tools either.

## AWS services used (1, S3 — see note below)

| Service | How Sentinel uses it |
|---|---|
| **Amazon S3** | Every resolved incident's postmortem is archived as a durable markdown artifact (`s3://bucket/postmortems/YYYY/MM/{incident}.md`) in addition to being embedded into memory. `backend/Sentinel.Core/Infra/Infra.cs`. |
| **AWS Lambda** *(deployable, see `lambda/AlertIngest`)* | CloudWatch Alarm → SNS → Lambda normalizes the alarm and forwards it to Sentinel's API — no human between an alarm firing and the agent reasoning. |

**A note on Amazon Bedrock.** Sentinel originally called Claude and Titan Text Embeddings V2 through Bedrock, and that code path still exists (`backend/Sentinel.Core/Agent/Bedrock.cs`). During the hackathon window, a brand-new AWS account hit an account-wide Bedrock on-demand token cap (confirmed directly in the Bedrock Playground, independent of any application code, across multiple models including Amazon's own Nova) with no self-service remedy available before the deadline. Rather than block the submission on an AWS support queue, the reasoning and embedding calls were moved to direct API calls — **Anthropic's Messages API** for reasoning and **Voyage AI's embeddings API** for vectors — while CockroachDB, S3, and `ccloud` stayed exactly as designed. The hackathon FAQ explicitly permits any model combination ("*you can use any combination of models*"), so this is a supported configuration, not a workaround of the rules. It's also arguably the more defensible engineering call under a real infrastructure constraint: not silently degrading, not blocking on a third party, swapping the dependency and shipping.

## Architecture

```
CloudWatch Alarm ──► SNS ──► Lambda (AlertIngest)
                                 │  POST /api/alerts
                                 ▼
                        Sentinel API (.NET 10)
                                 │
        ┌────────────────────────┼─────────────────────────┐
        ▼                        ▼                         ▼
  Anthropic API            CockroachDB Cloud           Amazon S3
  Claude (reasoning)       • incidents / events        postmortem
  Voyage AI (embeddings)   • agent_actions (audit)     artifacts
                           • memory_items (VECTOR
                             + distributed index)
                           • cluster_checks
                                 ▲
                          ccloud CLI (watchdog +
                          pre-mitigation state snapshots)

  React dashboard ◄── polls /api/incidents · /trace · /cluster/checks
```

## Repository layout

```
db/            schema.sql (vector + transactional), seed-corpus.json
backend/       .NET 10 solution — Sentinel.Api (host) + Sentinel.Core (agent)
lambda/        AlertIngest — AWS Lambda (SNS → Sentinel API)
tools/         MemorySeeder — embeds the seed corpus and loads memory
frontend/      React (Vite) mission-control dashboard
docs/          MCP setup, architecture notes
```

## Setup

Prerequisites: .NET 10 SDK, Node 18+, a free CockroachDB Cloud cluster, the `ccloud` CLI, an Anthropic API key (console.anthropic.com), a Voyage AI API key (voyageai.com), an AWS account for S3 (and optionally Lambda).

**1. CockroachDB Cloud**

Create a free cluster at cockroachlabs.cloud, then run the entire contents of `db/schema.sql` in the console's SQL Shell (or via `cockroach sql --url "$COCKROACH_CONN" -f db/schema.sql`).

**2. Environment**

```bash
export COCKROACH_CONN='Host=...;Port=26257;Database=sentinel;Username=...;Password=...;SSL Mode=VerifyFull'
export ANTHROPIC_API_KEY='sk-ant-...'
export VOYAGE_API_KEY='pa-...'
export AWS_ACCESS_KEY_ID='...'          # for S3
export AWS_SECRET_ACCESS_KEY='...'
export AWS_REGION='eu-central-1'
export SENTINEL_S3_BUCKET='your-bucket' # optional — postmortem artifacts
export CCLOUD_CLUSTER_ID='your-cluster-id'
```

**3. ccloud CLI**

Install per the official docs (cockroachlabs.com/docs/cockroachcloud/ccloud-get-started), then:

```bash
ccloud auth login
ccloud cluster info $CCLOUD_CLUSTER_ID -o json   # sanity check
```

**4. Seed the agent's memory** (6 postmortems/runbooks, embedded via Voyage)

```bash
dotnet run --project tools/MemorySeeder db/seed-corpus.json
```

**5. Run the API**

```bash
cd backend && dotnet run --project Sentinel.Api --urls http://localhost:5080
```

**6. Run the dashboard**

```bash
cd frontend && npm install && npm run dev
# open http://localhost:5173
```

**7. Fire a demo incident**

Click **Fire alert · api-gateway** in the dashboard, or:

```bash
curl -X POST http://localhost:5080/api/alerts -H 'Content-Type: application/json' -d '{
  "title": "pg: connection refused",
  "service": "api-gateway",
  "severity": "SEV1",
  "description": "api-gateway returning connection refused to the orders database; p99 above 4s; database CPU normal."
}'
```

Watch the trace: the agent recalls the pool-exhaustion postmortem (similarity meters), verifies pool saturation via telemetry, snapshots cluster state via ccloud, rolls back the deploy instead of restarting pods (its memory says restarts didn't work), verifies recovery across multiple metrics, resolves — and writes the new postmortem into memory and S3.

**Lambda deployment (production intake)**

```bash
cd lambda/AlertIngest
dotnet lambda deploy-function SentinelAlertIngest \
  --function-role your-lambda-role \
  --environment-variables SENTINEL_API_URL=https://your-sentinel-host
# Subscribe the function to your CloudWatch-alarms SNS topic.
```

## Production readiness

- **Audit**: every model call and tool invocation is an immutable row in `agent_actions` (input, output, model id, latency). The dashboard replays it verbatim.
- **Access control**: ccloud authenticates as a distinct identity and, per CockroachDB's own agent-security guidance, a triage-style agent should hold only read/inspect permissions — Sentinel's ccloud usage is read-only (`cluster info`) by design, never a config-mutating command.
- **Resilience**: memory survives node and region failure by construction (CockroachDB); the agent snapshots cluster state before mutating; on step-budget exhaustion it escalates to a human instead of flailing.
- **Blast-radius control**: one mitigation per step, verified by telemetry before resolution; the telemetry provider is swappable for CloudWatch/Prometheus.
- **Vendor resilience**: when Bedrock became unavailable mid-project, reasoning and embeddings moved behind the same interfaces to direct Anthropic/Voyage calls with no change to the memory layer, the tool-use loop, or the audit trail — the kind of dependency swap production systems need to survive.

## Demo telemetry note

`TelemetryProvider` returns deterministic, scripted metrics for the two demo scenarios so judges can reproduce the exact behavior in the video. It is the only simulated component; everything else (Anthropic calls, Voyage embeddings, vector recall, transactions, ccloud, S3) is real. Swap it for a CloudWatch client to run against live infrastructure.

## License

MIT — see [LICENSE](LICENSE).
