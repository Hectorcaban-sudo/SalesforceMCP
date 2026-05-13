import { useEffect, useState } from 'react'
import { Server, Check, AlertCircle } from 'lucide-react'
import { api } from '../api.js'
import { useToast } from '../Toast.jsx'

export default function SettingsView({ onConnected }) {
  const toast = useToast()
  const [cfg, setCfg] = useState({
    host: 'localhost', port: 8000, ssl: false,
    tenant: 'default_tenant', database: 'default_database',
    persist_path: '',
  })
  const [health, setHealth] = useState(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    (async () => {
      try {
        const [h, c] = await Promise.all([api.health(), api.getConfig()])
        setHealth(h)
        if (c) setCfg({ ...cfg, ...c, persist_path: c.persist_path || '' })
      } catch (e) { /* silent */ }
    })()
  }, []) // eslint-disable-line

  const submit = async () => {
    setBusy(true)
    try {
      const payload = { ...cfg }
      if (!payload.persist_path) payload.persist_path = null
      const res = await api.connect(payload)
      toast.success('Connected', `${payload.host}:${payload.port}`)
      setHealth({ status: 'ok', config: res.config })
      await onConnected?.()
    } catch (e) {
      toast.error('Connection failed', e.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="max-w-2xl">
      <div className="border-b border-ink-700/60 pb-4 mb-4">
        <h1 className="font-display text-4xl text-ink-50 tracking-tight">Connection</h1>
        <p className="text-xs text-ink-400 mt-1 font-mono">Point the dashboard at a ChromaDB server.</p>
      </div>

      <div className="panel p-5 space-y-4">
        <div className="flex items-center gap-2 pb-3 border-b border-ink-800">
          <Server size={14} className="text-accent" />
          <span className="text-sm font-semibold text-ink-100">ChromaDB server</span>
          <div className="flex-1" />
          {health?.status === 'ok' ? (
            <span className="tag-accent"><Check size={10} /> Connected</span>
          ) : (
            <span className="tag" style={{ color: '#fca5a5' }}><AlertCircle size={10} /> Disconnected</span>
          )}
        </div>

        <div className="grid grid-cols-3 gap-3">
          <div className="col-span-2">
            <label className="label block mb-1">Host</label>
            <input value={cfg.host} onChange={(e) => setCfg({ ...cfg, host: e.target.value })}
              className="input" />
          </div>
          <div>
            <label className="label block mb-1">Port</label>
            <input type="number" value={cfg.port} onChange={(e) => setCfg({ ...cfg, port: +e.target.value })}
              className="input" />
          </div>
        </div>

        <label className="flex items-center gap-2 text-xs">
          <input type="checkbox" checked={cfg.ssl} onChange={(e) => setCfg({ ...cfg, ssl: e.target.checked })}
            className="accent-accent" />
          <span className="text-ink-300">Use SSL (https)</span>
        </label>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="label block mb-1">Tenant</label>
            <input value={cfg.tenant} onChange={(e) => setCfg({ ...cfg, tenant: e.target.value })}
              className="input-mono" />
          </div>
          <div>
            <label className="label block mb-1">Database</label>
            <input value={cfg.database} onChange={(e) => setCfg({ ...cfg, database: e.target.value })}
              className="input-mono" />
          </div>
        </div>

        <details className="text-xs">
          <summary className="cursor-pointer text-ink-400 hover:text-ink-200">
            Or use a local persistent path instead
          </summary>
          <div className="mt-2">
            <label className="label block mb-1">Persist path</label>
            <input value={cfg.persist_path}
              onChange={(e) => setCfg({ ...cfg, persist_path: e.target.value })}
              className="input-mono" placeholder="/data/chroma" />
            <p className="text-[10px] text-ink-500 mt-1 font-mono">
              When set, the backend opens chromadb.PersistentClient instead of HttpClient.
            </p>
          </div>
        </details>

        <div className="pt-2 flex justify-end">
          <button onClick={submit} disabled={busy} className="btn-primary">
            {busy ? 'Connecting…' : 'Connect'}
          </button>
        </div>
      </div>

      {health?.error && (
        <div className="panel p-3 mt-3 border-red-900/60 text-xs text-red-300 font-mono">
          {health.error}
        </div>
      )}
    </div>
  )
}
