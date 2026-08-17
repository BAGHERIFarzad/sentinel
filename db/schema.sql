-- Sentinel — CockroachDB schema
-- One database holds BOTH the agent's transactional state and its semantic memory.
-- No separate vector store: embeddings live next to the operational data,
-- transactionally consistent with it.

CREATE DATABASE IF NOT EXISTS sentinel;
SET DATABASE = sentinel;

-- ── Transactional memory: incident state machine ────────────────────────────
CREATE TABLE IF NOT EXISTS incidents (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title         STRING NOT NULL,
    service       STRING NOT NULL,
    severity      STRING NOT NULL CHECK (severity IN ('SEV1','SEV2','SEV3')),
    status        STRING NOT NULL DEFAULT 'detected'
                  CHECK (status IN ('detected','diagnosing','mitigating','resolved','escalated')),
    signature     STRING NOT NULL,          -- normalized failure signature, e.g. "pg: connection refused api-gateway"
    alert_payload JSONB,
    resolution    STRING,                   -- filled by the agent on resolve
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    resolved_at   TIMESTAMPTZ,
    INDEX idx_incidents_status (status, created_at DESC),
    INDEX idx_incidents_service (service, created_at DESC)
);

-- Every state transition + observation, append-only.
CREATE TABLE IF NOT EXISTS incident_events (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_id UUID NOT NULL REFERENCES incidents (id) ON DELETE CASCADE,
    kind        STRING NOT NULL,            -- 'status_change' | 'observation' | 'mitigation' | 'note'
    payload     JSONB NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    INDEX idx_events_incident (incident_id, created_at)
);

-- ── Audit trail: every action the agent takes, immutable ────────────────────
CREATE TABLE IF NOT EXISTS agent_actions (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_id UUID REFERENCES incidents (id) ON DELETE SET NULL,
    step        INT NOT NULL,
    tool        STRING NOT NULL,            -- 'consult_memory' | 'query_metrics' | 'propose_mitigation' | 'snapshot_memory' | 'resolve_incident' | 'escalate'
    input       JSONB NOT NULL,
    output      JSONB,
    model_id    STRING NOT NULL,
    latency_ms  INT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    INDEX idx_actions_incident (incident_id, step)
);

-- ── Semantic memory: embeddings with distributed vector indexing ────────────
-- Titan Text Embeddings V2 → 1024 dimensions.
CREATE TABLE IF NOT EXISTS memory_items (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    kind        STRING NOT NULL CHECK (kind IN ('postmortem','runbook','incident')),
    incident_id UUID REFERENCES incidents (id) ON DELETE SET NULL,
    service     STRING,
    title       STRING NOT NULL,
    content     STRING NOT NULL,
    embedding   VECTOR(1024) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- CockroachDB distributed vector index (C-SPANN), declared inline so it
    -- works regardless of schema-changer mode. Semantic recall stays fast as
    -- the agent's memory grows — no reindexing, no separate vector database.
    VECTOR INDEX idx_memory_embedding (embedding)
);

-- ── Cluster self-management log (written by the ccloud watchdog) ────────────
CREATE TABLE IF NOT EXISTS cluster_checks (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    check_kind  STRING NOT NULL,            -- 'health' | 'backup' | 'audit_scan'
    result      JSONB NOT NULL,
    healthy     BOOL NOT NULL DEFAULT true,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    INDEX idx_cluster_checks_time (created_at DESC)
);
