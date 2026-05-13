import { useEffect, useState } from 'react'
import { Database, Layers, Cpu, ArrowUpRight, Box } from 'lucide-react'
import { api } from '../api.js'
import { useToast } from '../Toast.jsx'

export default function OverviewView({ onOpen, collections }) {
  const toast = useToast()
  const [overview, setOverview] = useState(null)
  const [activity, setActivity] = useState([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    (async () => {
      try {
        setLoading(true)
        const [ov, ac] = await Promise.all([api.overview(), api.activity(8)])
        setOverview(ov)
        setActivity(ac.events || [])
      } catch (e) {
        toast.error('Failed to load overview', e.message)
      } finally {
        setLoading(false)
      }
    })()
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="space-y-6 max-w-7xl">
      {/* Hero */}
      <div className="flex items-end justify-between border-b border-ink-700/60 pb-4">
        <div>
          <h1 className="font-display text-4xl text-ink-50 tracking-tight">Workspace</h1>
          <p className="text-xs text-ink-400 mt-1 font-mono">
            {loading ? 'Loading…' : `${overview?.collections_count ?? 0} collections · ${formatNum(overview?.total_records)} records`}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span className="tag-accent">
            <span className="w-1.5 h-1.5 rounded-full bg-accent" />
            live
          </span>
        </div>
      </div>

      {/* Stat cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        <StatCard icon={Layers} label="Collections" value={overview?.collections_count ?? '—'}
          accent />
        <StatCard icon={Database} label="Total records" value={formatNum(overview?.total_records)} />
        <StatCard icon={Cpu} label="Embedding dims"
          value={overview?.dimensions_seen?.length
            ? overview.dimensions_seen.join(' · ')
            : '—'} />
      </div>

      {/* Collections grid */}
      <section>
        <div className="flex items-end justify-between mb-3">
          <h2 className="font-display text-2xl text-ink-50">Collections</h2>
          <span className="text-[10px] text-ink-500 font-mono uppercase tracking-wider">
            Click to open
          </span>
        </div>
        {(!overview?.collections?.length) && (
          <div className="panel p-8 text-center text-ink-400 text-sm">
            No collections found. Create one from the sidebar to get started.
          </div>
        )}
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
          {overview?.collections?.map((c) => (
            <button
              key={c.name}
              onClick={() => onOpen(c.name)}
              className="panel group p-4 text-left hover:border-accent/40 hover:bg-ink-800/40 transition-all"
            >
              <div className="flex items-start justify-between mb-3">
                <div className="flex items-center gap-2 min-w-0">
                  <Box size={14} className="text-accent shrink-0" />
                  <span className="font-mono text-sm text-ink-50 truncate">{c.name}</span>
                </div>
                <ArrowUpRight size={14} className="text-ink-500 group-hover:text-accent transition-colors shrink-0" />
              </div>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <Field label="Records" value={formatNum(c.count)} />
                <Field label="Dim" value={c.dimension ?? '—'} mono />
              </div>
              {c.metadata_keys?.length > 0 && (
                <div className="mt-3 flex flex-wrap gap-1">
                  {c.metadata_keys.slice(0, 4).map((k) => (
                    <span key={k} className="tag">{k}</span>
                  ))}
                  {c.metadata_keys.length > 4 && (
                    <span className="tag">+{c.metadata_keys.length - 4}</span>
                  )}
                </div>
              )}
            </button>
          ))}
        </div>
      </section>

      {/* Recent activity */}
      <section>
        <div className="flex items-end justify-between mb-3">
          <h2 className="font-display text-2xl text-ink-50">Recent activity</h2>
        </div>
        <div className="panel divide-y divide-ink-700/60">
          {activity.length === 0 && (
            <div className="p-4 text-xs text-ink-500 italic">No activity yet.</div>
          )}
          {activity.map((ev, i) => (
            <div key={i} className="px-4 py-2 flex items-center gap-3 text-xs">
              <span className="font-mono text-[10px] text-ink-500 w-20 shrink-0">
                {timeAgo(ev.ts)}
              </span>
              <span className="tag">{ev.action}</span>
              <span className="font-mono text-ink-200 truncate flex-1">{ev.target || '—'}</span>
              <span className="text-ink-500 truncate hidden md:inline">{ev.detail}</span>
            </div>
          ))}
        </div>
      </section>
    </div>
  )
}

function StatCard({ icon: Icon, label, value, accent }) {
  return (
    <div className={`panel p-4 ${accent ? 'border-accent/30' : ''}`}>
      <div className="flex items-center justify-between mb-3">
        <span className="label">{label}</span>
        <Icon size={14} className={accent ? 'text-accent' : 'text-ink-400'} />
      </div>
      <div className="font-display text-3xl text-ink-50 tracking-tight">{value}</div>
    </div>
  )
}

function Field({ label, value, mono }) {
  return (
    <div>
      <div className="label">{label}</div>
      <div className={`text-ink-100 ${mono ? 'font-mono' : ''}`}>{value}</div>
    </div>
  )
}

function formatNum(n) {
  if (n == null) return '—'
  return n.toLocaleString()
}

function timeAgo(ts) {
  if (!ts) return '—'
  const diff = Date.now() / 1000 - ts
  if (diff < 60) return `${Math.floor(diff)}s ago`
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}
