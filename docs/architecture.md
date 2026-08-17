# Sentinel — architecture notes

## Memory design

One CockroachDB database holds four kinds of memory:

| Table | Memory type | Why it matters |
|---|---|---|
| `incidents` | Working memory / task state | The state machine the agent is executing right now |
| `incident_events` | Episodic log | Append-only history of everything observed and done |
| `agent_actions` | Audit memory | Immutable record of every model call and tool use |
| `memory_items` | Semantic long-term memory | Vector-indexed postmortems and runbooks the agent recalls |

Status transitions commit atomically with their event-log rows (single
transaction), so state and history can never disagree — including across
region failures.

## The learning loop

```
alert → embed → recall (vector index) → reason (Bedrock/Claude)
      → verify (telemetry) → snapshot memory (ccloud backup)
      → mitigate → verify → resolve
      → postmortem → S3 artifact + embed → memory_items
```

The write-back at the end is the point: the next similar alert recalls this
postmortem, including which mitigation worked and which did not.

## Failure behavior

- Bedrock unavailable → incident stays `detected`; alert is durably stored;
  Lambda retries via SNS redelivery/DLQ.
- ccloud unavailable → self-management degrades to logged no-ops; the agent
  keeps operating (backups are a safety enhancement, not a dependency).
- Step budget exhausted → explicit `escalated` state; a human takes over with
  the full audited trace.
- CockroachDB is the one component that must not fail — which is exactly why
  it is CockroachDB.
