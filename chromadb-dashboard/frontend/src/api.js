// Thin wrapper around fetch for the dashboard's REST API.
const BASE = '/api'

async function request(path, opts = {}) {
  const res = await fetch(BASE + path, {
    headers: { 'Content-Type': 'application/json' },
    ...opts,
  })
  if (!res.ok) {
    let msg = res.statusText
    try {
      const j = await res.json()
      msg = j.detail || j.error || JSON.stringify(j)
    } catch {}
    throw new Error(msg)
  }
  if (res.status === 204) return null
  const ct = res.headers.get('content-type') || ''
  if (ct.includes('application/json')) return res.json()
  return res.text()
}

export const api = {
  // Connection
  health: () => request('/health'),
  getConfig: () => request('/config'),
  connect: (cfg) => request('/connect', { method: 'POST', body: JSON.stringify(cfg) }),

  // Overview
  overview: () => request('/overview'),
  activity: (limit = 50) => request(`/activity?limit=${limit}`),

  // Collections
  listCollections: () => request('/collections'),
  createCollection: (name, metadata) =>
    request('/collections', { method: 'POST', body: JSON.stringify({ name, metadata }) }),
  getCollection: (name) => request(`/collections/${encodeURIComponent(name)}`),
  updateCollection: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}`, {
      method: 'PATCH',
      body: JSON.stringify(body),
    }),
  deleteCollection: (name) =>
    request(`/collections/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  cloneCollection: (name, new_name) =>
    request(`/collections/${encodeURIComponent(name)}/clone`, {
      method: 'POST',
      body: JSON.stringify({ new_name }),
    }),

  // Records
  listRecords: (name, { limit = 50, offset = 0, include_embeddings = false, where, where_document } = {}) => {
    const p = new URLSearchParams({ limit, offset, include_embeddings })
    if (where) p.set('where', JSON.stringify(where))
    if (where_document) p.set('where_document', JSON.stringify(where_document))
    return request(`/collections/${encodeURIComponent(name)}/records?${p}`)
  },
  getRecord: (name, id) =>
    request(`/collections/${encodeURIComponent(name)}/records/${encodeURIComponent(id)}`),
  addRecords: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}/records`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  upsertRecords: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}/upsert`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  updateRecord: (name, id, body) =>
    request(`/collections/${encodeURIComponent(name)}/records/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(body),
    }),
  bulkDelete: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}/records/bulk-delete`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  bulkMetadataPatch: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}/records/bulk-metadata`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  // Query
  queryText: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}/query/text`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  queryVector: (name, body) =>
    request(`/collections/${encodeURIComponent(name)}/query/vector`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  // Metrics
  metrics: (name, sample = 200) =>
    request(`/collections/${encodeURIComponent(name)}/metrics?sample=${sample}`),

  // CSV export
  exportCsvUrl: (name, { ids = [], include_embeddings = false } = {}) => {
    const p = new URLSearchParams({ include_embeddings })
    if (ids.length) p.set('ids', ids.join(','))
    return `${BASE}/collections/${encodeURIComponent(name)}/export.csv?${p}`
  },
}
