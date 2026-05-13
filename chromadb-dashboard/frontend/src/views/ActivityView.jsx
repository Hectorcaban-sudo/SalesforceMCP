import { useEffect, useState } from 'react'
import { RefreshCw, Activity as ActivityIcon } from 'lucide-react'
import { api } from '../api.js'
import { useToast } from '../Toast.jsx'

export default function ActivityView() {
  const toast = useToast()
  const [events, setEvents] = useState([])
  const [loading, setLoading] = useState(false)

  const load = async () => {
    setLoading(true)
    try {
      const data = await api.activity(200)
      setEvents(data.events || [])
    } catch (e) { toast.error('Failed to load activity', e.message) }
    finally { setLoading(false) }
  }

  useEffect(() => { load() }, []) // eslint-disable-line

  return (
    <div className="max-w-5xl">
      <div className="flex items-end justify-between border-b border-ink-700/60 pb-4 mb-4">
        <div>
          <h1 className="font-display text-4xl text-ink-50 tracking-tight">Activity</h1>
          <p className="text-xs text-ink-400 mt-1 font-mono">{events.length} events in this session</p>
        </div>
        <button onClick={load} className="btn">
          <RefreshCw size={11} className={loading ? 'animate-spin' : ''} /> Refresh
        </button>
      </div>

      <div className="panel divide-y divide-ink-700/60">
        {events.length === 0 && (
          <div className="p-8 text-center text-ink-500 text-sm">
            <ActivityIcon size={20} className="mx-auto mb-2 opacity-50" />
            No activity yet.
          </div>
        )}
        {events.map((ev, i) => (
          <div key={i} className="px-4 py-2.5 flex items-center gap-3 text-xs hover:bg-ink-900/50">
            <span className="font-mono text-[10px] text-ink-500 w-32 shrink-0">
              {new Date(ev.ts * 1000).toLocaleString()}
            </span>
            <span className="tag w-32 shrink-0 justify-center">{ev.action}</span>
            <span className="font-mono text-ink-200 truncate w-48 shrink-0">{ev.target || '—'}</span>
            <span className="text-ink-400 truncate flex-1">{ev.detail}</span>
          </div>
        ))}
      </div>
    </div>
  )
}
