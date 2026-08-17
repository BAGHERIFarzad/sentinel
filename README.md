# Sentinel — the agent that never forgets an outage

An autonomous SRE incident-response agent whose **entire memory lives in CockroachDB**, deployed on AWS. When an alert fires, Sentinel recalls every similar incident it has ever handled, reasons over live telemetry with Claude (via Amazon Bedrock), applies one mitigation at a time, and — when the incident is resolved — writes a postmortem back into its own memory. Every incident makes it smarter.

Sentinel also treats its memory as production infrastructure: a watchdog monitors the health of its own CockroachDB cluster through the **ccloud CLI**, and the agent snapshots its memory (backup via ccloud) before any mitigation that mutates state. An agent whose memory goes offline doesn't degrade gracefully — it stops. Sentinel is built around that fact.

Built for the **CockroachDB × AWS Hackathon — Build the Future of Agentic Memory**.

## Why memory is the product here

Incident response is exactly the workflow where agent memory is the difference between useful and useless:

- A stateless agent re-derives the same diagnosis every time and repeats mitigations that are *known not to work*.
- Sentinel's recalled postmortems explicitly encode "mitigation that worked" **and** "mitigation that did NOT work" — and its system prompt instructs it to trust that history. In the demo, the agent skips the pod-restart trap because a past postmortem says restarts only masked the pool leak.
- The memory is transactional **and** semantic in one system: the incident state machine, the immutable audit trail, and the vector-indexed postmortems are written by the same database, in the same transactions. No drift between "what happened" and "what it means."

## CockroachDB tools used (3 of 4)

| Tool | How Sentinel uses it |
|---|---|
| **Distributed Vector Indexing** | `memory_items.embedding VECTOR(1024)` under a `CREATE VECTOR INDEX`. Every alert is embedded (Titan V2) and semantically matched against all past postmortems and runbooks (`embedding <-> $1`). Resolved incidents are embedded and written back — the index grows with every outage, with no separate vector store. See `db/schema.sql`, `backend/Sentinel.Core/Memory/MemoryStore.cs`. |
| **ccloud CLI (agent-ready)** | The `CcloudWatchdog` background service polls `ccloud cluster get --json` every 60s and records health checks in `cluster_checks`. Before any `apply_mitigation`, the agent runs `ccloud cluster backup create --json` — it snapshots its own memory before mutating state. Runs under a service account with granular RBAC. See `backend/Sentinel.Core/Infra/Infra.cs`. |
| **Managed MCP Server** | Used during development and for safe operational introspection: the read-only MCP endpoint lets Claude Code inspect Sentinel's schema, query incident history, and verify audit rows without write access — with full audit logging on the CockroachDB side. Setup in `docs/mcp-setup.md`. |
| Transactional SQL (foundation) | Incident state machine (`detected → diagnosing → mitigating → resolved/escalated`), append-only `incident_events`, and immutable `agent_actions` audit trail — status changes and their event log commit in a single transaction. |

## AWS services used (3)

| Service | How Sentinel uses it |
|---|---|
| **Amazon Bedrock** | Claude (Converse API + tool use) is the reasoning engine; Titan Text Embeddings V2 produces the 1024-dim vectors for semantic memory. `backend/Sentinel.Core/Agent/Bedrock.cs`. |
| **AWS Lambda** | `lambda/AlertIngest` — CloudWatch Alarm → SNS → Lambda normalizes the alarm and forwards it to Sentinel's API. No human between an alarm firing and the agent reasoning. |
| **Amazon S3** | Every resolved incident's postmortem is archived as a durable markdown artifact (`s3://bucket/postmortems/YYYY/MM/{incident}.md`) in addition to being embedded into memory. |

## Architecture

```
CloudWatch Alarm ──► SNS ──► Lambda (AlertIngest)
                                 │  POST /api/alerts
                                 ▼
                        Sentinel API (.NET 10, ECS/EC2)
                                 │
        ┌────────────────────────┼─────────────────────────┐
        ▼                        ▼                         ▼
  Amazon Bedrock          CockroachDB Cloud           Amazon S3
  Claude (reasoning)      • incidents / events        postmortem
  Titan V2 (embeddings)   • agent_actions (audit)     artifacts
                          • memory_items (VECTOR
                            + distributed index)
                          • cluster_checks
                                 ▲
                          ccloud CLI (watchdog +
                          pre-mitigation backups)

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

Prerequisites: .NET 10 SDK, Node 18+, an AWS account with Bedrock model access (Claude + Titan Embed V2), a free CockroachDB Cloud cluster, the `ccloud` CLI (optional but recommended).

**1. CockroachDB Cloud**

```bash
# Create a free cluster at https://cockroachlabs.cloud, then:
cockroach sql --url "$COCKROACH_CONN" -f db/schema.sql
```

**2. Environment**

```bash
export COCKROACH_CONN='postgresql://user:pass@host:26257/sentinel?sslmode=verify-full'
export AWS_REGION='us-east-1'            # any Bedrock region with model access
export SENTINEL_S3_BUCKET='your-bucket'  # optional — postmortem artifacts
export CCLOUD_CLUSTER_ID='your-cluster'  # optional — enables ccloud self-management
```

**3. Seed the agent's memory** (6 postmortems/runbooks, embedded via Bedrock)

```bash
dotnet run --project tools/MemorySeeder db/seed-corpus.json
```

**4. Run the API**

```bash
cd backend && dotnet run --project Sentinel.Api --urls http://localhost:5080
```

**5. Run the dashboard**

```bash
cd frontend && npm install && npm run dev
# open http://localhost:5173
```

**6. Fire a demo incident**

Click **Fire alert · api-gateway** in the dashboard, or:

```bash
curl -X POST http://localhost:5080/api/alerts -H 'Content-Type: application/json' -d '{
  "title": "pg: connection refused",
  "service": "api-gateway",
  "severity": "SEV1",
  "description": "api-gateway returning connection refused to the orders database; p99 above 4s; database CPU normal."
}'
```

Watch the trace: the agent recalls the 2025-11-04 pool-exhaustion postmortem (similarity meters), verifies pool saturation via telemetry, snapshots its memory via ccloud, rolls back the deploy instead of restarting pods (its memory says restarts didn't work), verifies recovery, resolves — and writes the new postmortem into memory and S3.

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
- **Access control**: ccloud runs under a scoped service account (RBAC); the MCP endpoint is read-only; the DB user for the API has table-level grants only.
- **Resilience**: memory survives node and region failure by construction (CockroachDB); the agent snapshots before mutating; on step-budget exhaustion it escalates to a human instead of flailing.
- **Blast-radius control**: one mitigation per step, verified by telemetry before resolution; the telemetry provider is swappable for CloudWatch/Prometheus.

## Demo telemetry note

`TelemetryProvider` returns deterministic, scripted metrics for the two demo scenarios so judges can reproduce the exact behavior in the video. It is the only simulated component; everything else (Bedrock calls, vector recall, transactions, ccloud, S3) is real. Swap it for a CloudWatch client to run against live infrastructure.

## License

MIT — see [LICENSE](LICENSE).
