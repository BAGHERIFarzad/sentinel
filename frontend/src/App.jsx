import React, { useEffect, useState, useCallback } from 'react'
import { listIncidents, getTrace, getClusterChecks, fireDemoAlert } from './api.js'

const STATUS_LABEL = {
  detected: 'Detected',
  diagnosing: 'Diagnosing',
  mitigating: 'Mitigating',
  resolved: 'Resolved',
  escalated: 'Escalated'
}

const TOOL_LABEL = {
  consult_memory: 'Memory recall',
  query_metrics: 'Telemetry query',
  apply_mitigation: 'Mitigation',
  resolve_incident: 'Resolution',
  escalate: 'Escalation',
  snapshot_memory: 'Memory snapshot'
}

export default function App() {
  const [incidents, setIncidents] = useState([])
  const [selected, setSelected] = useState(null)
  const [trace, setTrace] = useState([])
  const [checks, setChecks] = useState([])
  const [firing, setFiring] = useState(false)

  const refresh = useCallback(async () => {
    try {
      const [inc, ch] = await Promise.all([listIncidents(), getClusterChecks()])
      setIncidents(inc)
      setChecks(ch)
      if (!selected && inc.length > 0) setSelected(inc[0].id)
    } catch { /* API not up yet */ }
  }, [selected])

  useEffect(() => {
    refresh()
    const t = setInterval(refresh, 3000)
    return () => clearInterval(t)
  }, [refresh])

  useEffect(() => {
    if (!selected) return
    let live = true
    const load = async () => {
      try { const tr = await getTrace(selected); if (live) setTrace(tr) } catch {}
    }
    load()
    const t = setInterval(load, 2500)
    return () => { live = false; clearInterval(t) }
  }, [selected])

  const fire = async (scenario) => {
    setFiring(true)
    try {
      const { incidentId } = await fireDemoAlert(scenario)
      setSelected(incidentId)
      await refresh()
    } finally { setFiring(false) }
  }

  const current = incidents.find(i => i.id === selected)

  return (
    <div className="shell">
      <header className="topbar">
        <div className="wordmark">
          <span className="mark" aria-hidden="true" />
          SENTINEL
        </div>
        <div className="tagline">The agent that never forgets an outage</div>
        <div className="fire-controls">
          <button disabled={firing} onClick={() => fire('gateway')}>Fire alert · api-gateway</button>
          <button disabled={firing} onClick={() => fire('checkout')}>Fire alert · checkout</button>
        </div>
      </header>

      <main className="grid">
        <section className="panel queue">
          <h2>Incident queue</h2>
          {incidents.length === 0 && (
            <p className="empty">No incidents yet. Fire a demo alert to wake the agent.</p>
          )}
          <ul>
            {incidents.map(i => (
              <li key={i.id}>
                <button
                  className={`incident ${i.id === selected ? 'active' : ''}`}
                  onClick={() => setSelected(i.id)}
                >
                  <span className={`sev sev-${i.severity?.toLowerCase()}`}>{i.severity}</span>
                  <span className="incident-title">{i.title}</span>
                  <span className="incident-meta">
                    <span className="service">{i.service}</span>
                    <span className={`status status-${i.status}`}>{STATUS_LABEL[i.status] || i.status}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </section>

        <section className="panel trace">
          <h2>
            Agent trace
            {current && <span className="trace-sub"> — {current.title}</span>}
          </h2>
          {trace.length === 0 && <p className="empty">Select an incident to replay the agent's audited reasoning.</p>}
          <ol className="ledger">
            {trace.map(a => (
              <TraceStep key={a.id} action={a} />
            ))}
          </ol>
          {current?.resolution && (
            <div className="resolution">
              <span className="resolution-label">Resolution</span>
              {current.resolution}
            </div>
          )}
        </section>

        <aside className="panel side">
          <h2>Memory layer</h2>
          <p className="side-note">
            Every step above is persisted in CockroachDB — incident state, audit trail,
            and postmortem embeddings in one transactionally consistent system.
          </p>
          <h3>Cluster self-management <span className="via">via ccloud</span></h3>
          <ul className="checks">
            {checks.length === 0 && <li className="empty">No cluster checks yet.</li>}
            {checks.slice(0, 8).map(c => (
              <li key={c.id} className={c.healthy ? 'ok' : 'bad'}>
                <span className="dot" aria-hidden="true" />
                <span className="check-kind">{c.checkKind}</span>
                <time>{new Date(c.createdAt).toLocaleTimeString()}</time>
              </li>
            ))}
          </ul>
        </aside>
      </main>
    </div>
  )
}

function TraceStep({ action }) {
  const output = safeParse(action.outputJson)
  const input = safeParse(action.inputJson)
  const isRecall = action.tool === 'consult_memory'
  const matches = isRecall && Array.isArray(output) ? output : null

  return (
    <li className={`step tool-${action.tool}`}>
      <div className="step-head">
        <span className="step-num">{String(action.step).padStart(2, '0')}</span>
        <span className="tool-chip">{TOOL_LABEL[action.tool] || action.tool}</span>
        <span className="latency">{action.latencyMs} ms</span>
      </div>

      {matches ? (
        <div className="recalls">
          {matches.map((m, idx) => (
            <div className="recall" key={idx}>
              <div className="recall-title">
                <span className={`kind kind-${m.kind || 'memory'}`}>{m.kind || 'memory'}</span>
                {m.title}
              </div>
              <div className="meter" role="img" aria-label={`similarity ${pct(m.similarity)}`}>
                <div className="meter-fill" style={{ width: pct(m.similarity) }} />
                <span className="meter-value">{pct(m.similarity)}</span>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <>
          {input && <pre className="io in">{compact(input)}</pre>}
          {output && <pre className="io out">{compact(output)}</pre>}
        </>
      )}
    </li>
  )
}

function safeParse(s) {
  if (!s) return null
  try { return JSON.parse(s) } catch { return s }
}

function pct(v) {
  const n = typeof v === 'number' ? v : 0
  return `${Math.max(0, Math.min(100, Math.round(n * 100)))}%`
}

function compact(obj) {
  const s = typeof obj === 'string' ? obj : JSON.stringify(obj, null, 1)
  return s.length > 400 ? s.slice(0, 400) + ' …' : s
}
