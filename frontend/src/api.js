const base = ''

export async function listIncidents() {
  const r = await fetch(`${base}/api/incidents`)
  return r.json()
}

export async function getTrace(id) {
  const r = await fetch(`${base}/api/incidents/${id}/trace`)
  return r.json()
}

export async function getClusterChecks() {
  const r = await fetch(`${base}/api/cluster/checks`)
  return r.json()
}

export async function fireDemoAlert(scenario) {
  const scenarios = {
    gateway: {
      title: 'pg: connection refused',
      service: 'api-gateway',
      severity: 'SEV1',
      description: 'api-gateway returning connection refused errors to the orders database; p99 latency above 4s. Database CPU appears normal.'
    },
    checkout: {
      title: 'CrashLoopBackOff — OOMKilled',
      service: 'checkout',
      severity: 'SEV2',
      description: 'checkout pods restarting every 3-4 minutes with exit code 137 during elevated promo traffic.'
    }
  }
  const r = await fetch(`${base}/api/alerts`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(scenarios[scenario])
  })
  return r.json()
}
