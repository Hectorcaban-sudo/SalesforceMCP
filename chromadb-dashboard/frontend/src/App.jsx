import { useEffect, useState, useCallback } from 'react'
import {
  Database, LayoutDashboard, Search, Activity, Settings, ChevronRight,
  Plus, RefreshCw, Server, AlertCircle, Box,
} from 'lucide-react'
import { ToastProvider, useToast } from './Toast.jsx'
import { api } from './api.js'
import OverviewView from './views/OverviewView.jsx'
import CollectionView from './views/CollectionView.jsx'
import ActivityView from './views/ActivityView.jsx'
import SettingsView from './views/SettingsView.jsx'
import Modal from './Modal.jsx'

export default function App() {
  return (
    <ToastProvider>
      <Shell />
    </ToastProvider>
  )
}

function Shell() {
  const toast = useToast()
  const [view, setView] = useState({ kind: 'overview' }) // {kind:'overview'} | {kind:'collection', name} | {kind:'activity'} | {kind:'settings'}
  const [collections, setCollections] = useState([])
  const [health, setHealth] = useState({ status: 'loading' })
  const [showNew, setShowNew] = useState(false)
  const [loading, setLoading] = useState(false)

  const refreshCollections = useCallback(async () => {
    try {
      const data = await api.listCollections()
      setCollections(data.collections || [])
    } catch (e) {
      // Don't toast on initial load failure; settings page will help
    }
  }, [])

  const refreshHealth = useCallback(async () => {
    try {
      const h = await api.health()
      setHealth(h)
    } catch (e) {
      setHealth({ status: 'error', error: e.message })
    }
  }, [])

  useEffect(() => {
    (async () => {
      setLoading(true)
      await refreshHealth()
      await refreshCollections()
      setLoading(false)
    })()
  }, [refreshHealth, refreshCollections])

  const onCreateCollection = async (name, metadata) => {
    try {
      await api.createCollection(name, metadata)
      toast.success('Collection created', name)
      setShowNew(false)
      await refreshCollections()
      setView({ kind: 'collection', name })
    } catch (e) {
      toast.error('Failed to create collection', e.message)
    }
  }

  const headerStatus = (() => {
    if (health.status === 'ok') return { dot: 'bg-accent', label: 'Connected' }
    if (health.status === 'loading') return { dot: 'bg-ink-500', label: 'Connecting…' }
    return { dot: 'bg-red-500', label: 'Disconnected' }
  })()

  return (
    <div className="min-h-screen bg-ink-950 bg-grid">
      <div className="flex h-screen">
        {/* Sidebar */}
        <aside className="w-64 shrink-0 border-r border-ink-700/60 bg-ink-950/80 flex flex-col">
          {/* Logo */}
          <div className="px-4 py-4 border-b border-ink-700/60 flex items-center gap-2.5">
            <div className="w-7 h-7 rounded bg-accent flex items-center justify-center font-mono font-bold text-ink-950">C</div>
            <div className="flex-1 min-w-0">
              <div className="text-sm font-semibold leading-tight text-ink-50">ChromaDB</div>
              <div className="text-[10px] text-ink-400 leading-tight font-mono">dashboard.v1</div>
            </div>
            <div className={`w-2 h-2 rounded-full ${headerStatus.dot} ${health.status === 'ok' ? 'pulse-dot' : ''}`} title={headerStatus.label} />
          </div>

          {/* Nav */}
          <nav className="px-2 py-3 space-y-0.5">
            <NavItem icon={LayoutDashboard} label="Overview" active={view.kind === 'overview'} onClick={() => setView({ kind: 'overview' })} />
            <NavItem icon={Activity} label="Activity" active={view.kind === 'activity'} onClick={() => setView({ kind: 'activity' })} />
            <NavItem icon={Settings} label="Connection" active={view.kind === 'settings'} onClick={() => setView({ kind: 'settings' })} />
          </nav>

          {/* Collections */}
          <div className="px-3 mt-2 mb-1.5 flex items-center justify-between">
            <span className="label">Collections</span>
            <div className="flex items-center gap-1">
              <button onClick={refreshCollections} className="text-ink-400 hover:text-ink-100 p-1" title="Refresh">
                <RefreshCw size={11} />
              </button>
              <button onClick={() => setShowNew(true)} className="text-accent hover:text-accent-glow p-1" title="New collection">
                <Plus size={13} />
              </button>
            </div>
          </div>
          <div className="flex-1 overflow-y-auto px-2 pb-2 space-y-0.5">
            {collections.length === 0 && (
              <div className="px-2.5 py-3 text-[11px] text-ink-500 italic">
                No collections yet.
              </div>
            )}
            {collections.map((c) => (
              <button
                key={c.name}
                onClick={() => setView({ kind: 'collection', name: c.name })}
                className={`w-full text-left flex items-center gap-2 px-2.5 py-1.5 rounded text-xs transition-colors group ${
                  view.kind === 'collection' && view.name === c.name
                    ? 'bg-accent/10 text-accent border border-accent/20'
                    : 'text-ink-200 hover:bg-ink-800/60 border border-transparent'
                }`}
              >
                <Box size={12} className="shrink-0" />
                <span className="truncate flex-1 font-mono">{c.name}</span>
                <span className="text-[10px] text-ink-500 font-mono">{formatCount(c.count)}</span>
              </button>
            ))}
          </div>

          {/* Footer */}
          <div className="border-t border-ink-700/60 px-3 py-2 text-[10px] text-ink-500 font-mono flex items-center gap-2">
            <Server size={10} />
            <span className="truncate flex-1">{health.config?.host}:{health.config?.port}</span>
          </div>
        </aside>

        {/* Main content */}
        <main className="flex-1 overflow-y-auto">
          {/* Top bar */}
          <header className="sticky top-0 z-20 bg-ink-950/80 backdrop-blur-md border-b border-ink-700/60 px-6 py-2.5 flex items-center gap-3">
            <Breadcrumbs view={view} setView={setView} />
            <div className="flex-1" />
            {health.status === 'error' && (
              <div className="flex items-center gap-1.5 text-[11px] text-red-300">
                <AlertCircle size={12} />
                <span className="font-mono">{health.error || 'Disconnected'}</span>
              </div>
            )}
            <span className="kbd">{health.config?.tenant || '—'}</span>
            <span className="text-ink-600">/</span>
            <span className="kbd">{health.config?.database || '—'}</span>
          </header>

          {loading && <div className="h-0.5 shimmer" />}

          {/* View */}
          <div className="p-6">
            {view.kind === 'overview' && (
              <OverviewView onOpen={(name) => setView({ kind: 'collection', name })} collections={collections} />
            )}
            {view.kind === 'collection' && (
              <CollectionView
                key={view.name}
                name={view.name}
                onRefreshCollections={refreshCollections}
                onGoOverview={() => setView({ kind: 'overview' })}
              />
            )}
            {view.kind === 'activity' && <ActivityView />}
            {view.kind === 'settings' && <SettingsView onConnected={async () => { await refreshHealth(); await refreshCollections() }} />}
          </div>
        </main>
      </div>

      <NewCollectionModal open={showNew} onClose={() => setShowNew(false)} onCreate={onCreateCollection} />
    </div>
  )
}

function NavItem({ icon: Icon, label, active, onClick }) {
  return (
    <button
      onClick={onClick}
      className={`w-full flex items-center gap-2.5 px-2.5 py-1.5 rounded text-xs transition-colors ${
        active ? 'bg-ink-800 text-ink-50' : 'text-ink-300 hover:bg-ink-800/60 hover:text-ink-100'
      }`}
    >
      <Icon size={13} />
      <span>{label}</span>
    </button>
  )
}

function Breadcrumbs({ view, setView }) {
  const crumbs = []
  crumbs.push({ label: 'Dashboard', onClick: () => setView({ kind: 'overview' }) })
  if (view.kind === 'collection') {
    crumbs.push({ label: 'Collections' })
    crumbs.push({ label: view.name, active: true })
  } else if (view.kind === 'activity') {
    crumbs.push({ label: 'Activity', active: true })
  } else if (view.kind === 'settings') {
    crumbs.push({ label: 'Connection', active: true })
  }
  return (
    <div className="flex items-center gap-1.5 text-xs">
      {crumbs.map((c, i) => (
        <span key={i} className="flex items-center gap-1.5">
          {i > 0 && <ChevronRight size={11} className="text-ink-600" />}
          <button
            onClick={c.onClick}
            className={`${c.active ? 'text-ink-50 font-semibold' : 'text-ink-400 hover:text-ink-100'} ${c.onClick ? '' : 'cursor-default'} font-mono`}
            disabled={!c.onClick}
          >
            {c.label}
          </button>
        </span>
      ))}
    </div>
  )
}

function NewCollectionModal({ open, onClose, onCreate }) {
  const [name, setName] = useState('')
  const [metadata, setMetadata] = useState('')
  useEffect(() => { if (open) { setName(''); setMetadata('') } }, [open])

  const submit = () => {
    if (!name.trim()) return
    let meta = null
    if (metadata.trim()) {
      try { meta = JSON.parse(metadata) } catch { /* ignore */ }
    }
    onCreate(name.trim(), meta)
  }

  return (
    <Modal open={open} onClose={onClose} title="New collection"
      subtitle="Names must be 3–63 chars, start/end with alphanumeric, and use only [a–z0–9._-]."
      footer={
        <>
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={submit} disabled={!name.trim()} className="btn-primary">Create</button>
        </>
      }>
      <div className="space-y-3">
        <div>
          <label className="label block mb-1">Name</label>
          <input value={name} onChange={(e) => setName(e.target.value)} autoFocus
            className="input-mono" placeholder="my-collection" />
        </div>
        <div>
          <label className="label block mb-1">Metadata (JSON, optional)</label>
          <textarea value={metadata} onChange={(e) => setMetadata(e.target.value)} rows={4}
            className="input-mono" placeholder='{"description": "..."}' />
        </div>
      </div>
    </Modal>
  )
}

function formatCount(n) {
  if (n == null) return '—'
  if (n < 1000) return String(n)
  if (n < 1_000_000) return (n / 1000).toFixed(n < 10_000 ? 1 : 0) + 'k'
  return (n / 1_000_000).toFixed(1) + 'M'
}
