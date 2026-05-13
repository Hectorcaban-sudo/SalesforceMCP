import { useEffect, useMemo, useState, useCallback } from 'react'
import {
  ChevronLeft, ChevronRight, RefreshCw, Plus, Trash2, Edit3, Search,
  Download, Filter, Settings2, AlertTriangle, Eye, X, Copy, Tag,
  BarChart3, Sparkles, Database as Database2Icon,
} from 'lucide-react'
import { api } from '../api.js'
import { useToast } from '../Toast.jsx'
import Modal from '../Modal.jsx'

export default function CollectionView({ name, onRefreshCollections, onGoOverview }) {
  const toast = useToast()
  const [info, setInfo] = useState(null)
  const [records, setRecords] = useState([])
  const [total, setTotal] = useState(0)
  const [limit, setLimit] = useState(50)
  const [offset, setOffset] = useState(0)
  const [loading, setLoading] = useState(false)
  const [selected, setSelected] = useState(new Set())
  const [where, setWhere] = useState('') // JSON text
  const [whereDoc, setWhereDoc] = useState('')
  const [showFilters, setShowFilters] = useState(false)

  const [activeTab, setActiveTab] = useState('records') // records | query | metrics | settings
  const [editRec, setEditRec] = useState(null)
  const [showAdd, setShowAdd] = useState(false)
  const [showRename, setShowRename] = useState(false)
  const [showClone, setShowClone] = useState(false)
  const [showDeleteCol, setShowDeleteCol] = useState(false)
  const [showBulkMeta, setShowBulkMeta] = useState(false)
  const [showBulkDelete, setShowBulkDelete] = useState(false)

  // -- Loaders --
  const loadInfo = useCallback(async () => {
    try {
      const data = await api.getCollection(name)
      setInfo(data)
    } catch (e) { toast.error('Failed to load collection', e.message) }
  }, [name, toast])

  const loadRecords = useCallback(async () => {
    setLoading(true)
    try {
      let wh, wd
      if (where.trim()) {
        try { wh = JSON.parse(where) } catch { toast.warn('Invalid where filter JSON'); setLoading(false); return }
      }
      if (whereDoc.trim()) {
        try { wd = JSON.parse(whereDoc) } catch { toast.warn('Invalid where_document filter JSON'); setLoading(false); return }
      }
      const data = await api.listRecords(name, { limit, offset, where: wh, where_document: wd })
      setRecords(data.records || [])
      setTotal(data.total ?? 0)
    } catch (e) {
      toast.error('Failed to load records', e.message)
    } finally {
      setLoading(false)
    }
  }, [name, limit, offset, where, whereDoc, toast])

  useEffect(() => { loadInfo() }, [loadInfo])
  useEffect(() => { loadRecords() }, [loadRecords])
  useEffect(() => { setSelected(new Set()); setOffset(0) }, [name])

  // -- Computed --
  const metadataKeys = useMemo(() => {
    const set = new Set()
    records.forEach((r) => r.metadata && Object.keys(r.metadata).forEach((k) => set.add(k)))
    return Array.from(set)
  }, [records])

  const allSelected = records.length > 0 && records.every((r) => selected.has(r.id))
  const toggleAll = () => {
    if (allSelected) setSelected(new Set())
    else setSelected(new Set(records.map((r) => r.id)))
  }
  const toggleOne = (id) => {
    const s = new Set(selected)
    s.has(id) ? s.delete(id) : s.add(id)
    setSelected(s)
  }

  // -- Actions --
  const doDeleteSelected = async () => {
    try {
      await api.bulkDelete(name, { ids: Array.from(selected) })
      toast.success(`Deleted ${selected.size} records`)
      setSelected(new Set())
      setShowBulkDelete(false)
      await loadRecords()
      await loadInfo()
    } catch (e) {
      toast.error('Bulk delete failed', e.message)
    }
  }

  const doDeleteCollection = async () => {
    try {
      await api.deleteCollection(name)
      toast.success('Collection deleted', name)
      setShowDeleteCol(false)
      await onRefreshCollections()
      onGoOverview()
    } catch (e) {
      toast.error('Delete failed', e.message)
    }
  }

  const downloadCsv = (ids = []) => {
    const url = api.exportCsvUrl(name, { ids, include_embeddings: false })
    window.open(url, '_blank')
  }

  // -- Render --
  return (
    <div className="space-y-4 max-w-[1600px]">
      {/* Header */}
      <div className="flex items-end justify-between border-b border-ink-700/60 pb-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <h1 className="font-mono text-2xl text-ink-50 truncate">{name}</h1>
            <button
              onClick={() => { navigator.clipboard.writeText(name); toast.success('Name copied') }}
              className="text-ink-500 hover:text-ink-200 p-1"
              title="Copy name"
            >
              <Copy size={12} />
            </button>
          </div>
          <p className="text-xs text-ink-400 font-mono">
            {info?.count?.toLocaleString() ?? '—'} records ·
            id <span className="text-ink-300">{info?.id || '—'}</span>
          </p>
        </div>
        <div className="flex items-center gap-1.5">
          <button onClick={() => setShowAdd(true)} className="btn-primary">
            <Plus size={12} /> Add record
          </button>
          <button onClick={() => setShowRename(true)} className="btn">
            <Edit3 size={12} /> Rename
          </button>
          <button onClick={() => setShowClone(true)} className="btn">
            <Copy size={12} /> Clone
          </button>
          <button onClick={() => setShowDeleteCol(true)} className="btn-danger">
            <Trash2 size={12} /> Delete
          </button>
        </div>
      </div>

      {/* Tabs */}
      <div className="flex items-center gap-1 border-b border-ink-700/60">
        <Tab active={activeTab === 'records'} onClick={() => setActiveTab('records')} icon={Database2Icon} label="Records" />
        <Tab active={activeTab === 'query'} onClick={() => setActiveTab('query')} icon={Sparkles} label="Query" />
        <Tab active={activeTab === 'metrics'} onClick={() => setActiveTab('metrics')} icon={BarChart3} label="Metrics" />
        <Tab active={activeTab === 'settings'} onClick={() => setActiveTab('settings')} icon={Settings2} label="Settings" />
      </div>

      {/* TAB: Records */}
      {activeTab === 'records' && (
        <>
          {/* Toolbar */}
          <div className="flex items-center gap-2 flex-wrap">
            <button onClick={loadRecords} className="btn">
              <RefreshCw size={11} className={loading ? 'animate-spin' : ''} /> Refresh
            </button>
            <button onClick={() => setShowFilters((v) => !v)} className={`btn ${showFilters || where || whereDoc ? 'border-accent/40 text-accent' : ''}`}>
              <Filter size={11} /> Filters
              {(where || whereDoc) && <span className="w-1.5 h-1.5 rounded-full bg-accent" />}
            </button>
            <button onClick={() => downloadCsv()} className="btn">
              <Download size={11} /> Export CSV
            </button>
            <div className="flex-1" />
            {selected.size > 0 && (
              <>
                <span className="text-xs text-ink-400 font-mono">
                  {selected.size} selected
                </span>
                <button onClick={() => downloadCsv(Array.from(selected))} className="btn">
                  <Download size={11} /> Export selection
                </button>
                <button onClick={() => setShowBulkMeta(true)} className="btn">
                  <Tag size={11} /> Patch metadata
                </button>
                <button onClick={() => setShowBulkDelete(true)} className="btn-danger">
                  <Trash2 size={11} /> Delete
                </button>
              </>
            )}
          </div>

          {/* Filters panel */}
          {showFilters && (
            <div className="panel p-3 grid grid-cols-1 md:grid-cols-2 gap-3">
              <div>
                <label className="label block mb-1">where (metadata filter, JSON)</label>
                <textarea value={where} onChange={(e) => setWhere(e.target.value)} rows={3}
                  className="input-mono" placeholder='{"category": {"$eq": "docs"}}' />
              </div>
              <div>
                <label className="label block mb-1">where_document (document filter, JSON)</label>
                <textarea value={whereDoc} onChange={(e) => setWhereDoc(e.target.value)} rows={3}
                  className="input-mono" placeholder='{"$contains": "search term"}' />
              </div>
              <div className="md:col-span-2 flex justify-end gap-2">
                <button onClick={() => { setWhere(''); setWhereDoc(''); setOffset(0) }} className="btn">Clear</button>
                <button onClick={() => { setOffset(0); loadRecords() }} className="btn-primary">Apply</button>
              </div>
            </div>
          )}

          {/* Table */}
          <div className="panel overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr className="border-b border-ink-700/60 bg-ink-950/60">
                    <th className="px-3 py-2 w-8">
                      <input type="checkbox" checked={allSelected}
                        onChange={toggleAll}
                        className="accent-accent" />
                    </th>
                    <th className="px-3 py-2 text-left label">ID</th>
                    <th className="px-3 py-2 text-left label">Document</th>
                    {metadataKeys.map((k) => (
                      <th key={k} className="px-3 py-2 text-left label whitespace-nowrap">
                        {k}
                      </th>
                    ))}
                    <th className="px-3 py-2 w-12"></th>
                  </tr>
                </thead>
                <tbody>
                  {records.length === 0 && !loading && (
                    <tr>
                      <td colSpan={3 + metadataKeys.length} className="px-4 py-8 text-center text-ink-500">
                        No records.
                      </td>
                    </tr>
                  )}
                  {records.map((r) => (
                    <tr key={r.id}
                      className={`border-b border-ink-800/60 hover:bg-ink-900/60 group ${selected.has(r.id) ? 'bg-accent/5' : ''}`}>
                      <td className="px-3 py-2">
                        <input type="checkbox" checked={selected.has(r.id)}
                          onChange={() => toggleOne(r.id)} className="accent-accent" />
                      </td>
                      <td className="px-3 py-2 font-mono text-ink-300 max-w-[200px] truncate">
                        {r.id}
                      </td>
                      <td className="px-3 py-2 text-ink-200 max-w-md">
                        <div className="truncate">{r.document || <span className="text-ink-600 italic">—</span>}</div>
                      </td>
                      {metadataKeys.map((k) => (
                        <td key={k} className="px-3 py-2 font-mono text-ink-300 whitespace-nowrap max-w-[160px] truncate">
                          {formatMetaCell(r.metadata?.[k])}
                        </td>
                      ))}
                      <td className="px-3 py-2 text-right">
                        <button onClick={() => setEditRec(r.id)} className="text-ink-500 hover:text-accent opacity-0 group-hover:opacity-100 transition-opacity">
                          <Eye size={13} />
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* Pagination */}
            <div className="border-t border-ink-700/60 px-3 py-2 flex items-center gap-3 text-xs">
              <span className="text-ink-400 font-mono">
                {total ? `${offset + 1}–${Math.min(offset + limit, total)} of ${total.toLocaleString()}` : '0 records'}
              </span>
              <div className="flex-1" />
              <select value={limit} onChange={(e) => { setLimit(+e.target.value); setOffset(0) }}
                className="input w-auto py-1 text-[11px]">
                <option value={25}>25 / page</option>
                <option value={50}>50 / page</option>
                <option value={100}>100 / page</option>
                <option value={250}>250 / page</option>
              </select>
              <button onClick={() => setOffset(Math.max(0, offset - limit))}
                disabled={offset === 0} className="btn">
                <ChevronLeft size={12} />
              </button>
              <button onClick={() => setOffset(offset + limit)}
                disabled={offset + limit >= total} className="btn">
                <ChevronRight size={12} />
              </button>
            </div>
          </div>
        </>
      )}

      {/* TAB: Query */}
      {activeTab === 'query' && <QueryPanel name={name} onOpenRecord={(id) => setEditRec(id)} />}

      {/* TAB: Metrics */}
      {activeTab === 'metrics' && <MetricsPanel name={name} />}

      {/* TAB: Settings */}
      {activeTab === 'settings' && (
        <CollectionSettings info={info} onSave={async (newMeta) => {
          try {
            await api.updateCollection(name, { new_metadata: newMeta })
            toast.success('Metadata updated')
            await loadInfo()
          } catch (e) { toast.error('Failed to update', e.message) }
        }} />
      )}

      {/* Modals */}
      {editRec && (
        <RecordEditor
          collectionName={name}
          recordId={editRec}
          onClose={() => setEditRec(null)}
          onSaved={async () => { setEditRec(null); await loadRecords() }}
          onDeleted={async () => { setEditRec(null); await loadRecords(); await loadInfo() }}
        />
      )}
      <AddRecordsModal open={showAdd} onClose={() => setShowAdd(false)}
        collectionName={name}
        onSaved={async () => { setShowAdd(false); await loadRecords(); await loadInfo() }} />
      <RenameModal open={showRename} onClose={() => setShowRename(false)}
        currentName={name}
        onRename={async (newName) => {
          try {
            await api.updateCollection(name, { new_name: newName })
            toast.success('Renamed', `${name} → ${newName}`)
            setShowRename(false)
            await onRefreshCollections()
            // Stay on overview to avoid stale state
            onGoOverview()
          } catch (e) { toast.error('Rename failed', e.message) }
        }} />
      <CloneModal open={showClone} onClose={() => setShowClone(false)}
        currentName={name}
        onClone={async (newName) => {
          try {
            const res = await api.cloneCollection(name, newName)
            toast.success('Cloned', `${newName} (${res.count} records)`)
            setShowClone(false)
            await onRefreshCollections()
          } catch (e) { toast.error('Clone failed', e.message) }
        }} />
      <ConfirmModal open={showDeleteCol} onClose={() => setShowDeleteCol(false)}
        title="Delete collection?" body={`This permanently deletes "${name}" and all its records.`}
        confirmLabel="Delete forever" onConfirm={doDeleteCollection} danger />
      <ConfirmModal open={showBulkDelete} onClose={() => setShowBulkDelete(false)}
        title={`Delete ${selected.size} records?`} body="This action cannot be undone."
        confirmLabel="Delete" onConfirm={doDeleteSelected} danger />
      <BulkMetadataModal open={showBulkMeta} onClose={() => setShowBulkMeta(false)}
        count={selected.size}
        onApply={async (patch) => {
          try {
            await api.bulkMetadataPatch(name, { ids: Array.from(selected), metadata_patch: patch })
            toast.success('Metadata patched', `${selected.size} records`)
            setShowBulkMeta(false)
            await loadRecords()
          } catch (e) { toast.error('Patch failed', e.message) }
        }} />
    </div>
  )
}

function Tab({ active, onClick, icon: Icon, label }) {
  return (
    <button onClick={onClick}
      className={`flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium border-b-2 transition-colors ${
        active ? 'border-accent text-accent' : 'border-transparent text-ink-400 hover:text-ink-100'
      }`}>
      <Icon size={12} /> {label}
    </button>
  )
}

function formatMetaCell(v) {
  if (v == null) return <span className="text-ink-600">—</span>
  if (typeof v === 'object') return <span className="text-ink-400">{JSON.stringify(v)}</span>
  if (typeof v === 'boolean') return <span className={v ? 'text-accent' : 'text-amber-400'}>{String(v)}</span>
  return String(v)
}

// =====================================================================
// Query Panel
// =====================================================================
function QueryPanel({ name, onOpenRecord }) {
  const toast = useToast()
  const [mode, setMode] = useState('text') // text | vector
  const [queryText, setQueryText] = useState('')
  const [queryVector, setQueryVector] = useState('')
  const [n, setN] = useState(10)
  const [where, setWhere] = useState('')
  const [whereDoc, setWhereDoc] = useState('')
  const [hits, setHits] = useState([])
  const [running, setRunning] = useState(false)

  const run = async () => {
    setRunning(true)
    try {
      let wh, wd
      if (where.trim()) wh = JSON.parse(where)
      if (whereDoc.trim()) wd = JSON.parse(whereDoc)

      let res
      if (mode === 'text') {
        if (!queryText.trim()) { toast.warn('Enter a query text'); return }
        res = await api.queryText(name, {
          query_texts: [queryText],
          n_results: n,
          where: wh, where_document: wd,
        })
      } else {
        let vec
        try { vec = JSON.parse(queryVector) } catch { toast.warn('Embedding must be a JSON array'); return }
        if (!Array.isArray(vec)) { toast.warn('Embedding must be a JSON array'); return }
        res = await api.queryVector(name, {
          query_embeddings: [vec],
          n_results: n,
          where: wh, where_document: wd,
        })
      }
      setHits(res.hits || [])
      if (!res.hits?.length) toast.info('No results')
    } catch (e) {
      toast.error('Query failed', e.message)
    } finally {
      setRunning(false)
    }
  }

  return (
    <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
      <div className="panel p-4 space-y-3">
        <div className="flex items-center gap-2">
          <h3 className="font-display text-xl text-ink-50">Query</h3>
          <div className="flex-1" />
          <div className="flex items-center gap-0 rounded-md border border-ink-700 overflow-hidden text-xs">
            <button onClick={() => setMode('text')}
              className={`px-2.5 py-1 ${mode === 'text' ? 'bg-accent/15 text-accent' : 'text-ink-400 hover:text-ink-100'}`}>
              Text
            </button>
            <button onClick={() => setMode('vector')}
              className={`px-2.5 py-1 ${mode === 'vector' ? 'bg-accent/15 text-accent' : 'text-ink-400 hover:text-ink-100'}`}>
              Vector
            </button>
          </div>
        </div>

        {mode === 'text' ? (
          <div>
            <label className="label block mb-1">Search text</label>
            <textarea value={queryText} onChange={(e) => setQueryText(e.target.value)}
              rows={3} className="input" placeholder="What are you looking for?" />
            <p className="text-[10px] text-ink-500 mt-1 font-mono">
              Uses the collection's configured embedding function on the server.
            </p>
          </div>
        ) : (
          <div>
            <label className="label block mb-1">Embedding (JSON array)</label>
            <textarea value={queryVector} onChange={(e) => setQueryVector(e.target.value)}
              rows={5} className="input-mono" placeholder="[0.123, -0.456, ...]" />
          </div>
        )}

        <div>
          <label className="label block mb-1">n_results</label>
          <input type="number" min={1} max={100} value={n}
            onChange={(e) => setN(+e.target.value)} className="input w-24" />
        </div>
        <details className="text-xs">
          <summary className="cursor-pointer text-ink-400 hover:text-ink-200">Advanced filters</summary>
          <div className="mt-2 space-y-2">
            <div>
              <label className="label block mb-1">where</label>
              <textarea value={where} onChange={(e) => setWhere(e.target.value)}
                rows={2} className="input-mono" placeholder='{"category": "docs"}' />
            </div>
            <div>
              <label className="label block mb-1">where_document</label>
              <textarea value={whereDoc} onChange={(e) => setWhereDoc(e.target.value)}
                rows={2} className="input-mono" placeholder='{"$contains": "term"}' />
            </div>
          </div>
        </details>

        <button onClick={run} disabled={running} className="btn-primary w-full justify-center">
          <Search size={12} /> {running ? 'Running…' : 'Run query'}
        </button>
      </div>

      <div className="panel p-0 overflow-hidden">
        <div className="px-4 py-2.5 border-b border-ink-700/60 flex items-center justify-between">
          <h3 className="font-display text-xl text-ink-50">Results</h3>
          <span className="text-[11px] text-ink-400 font-mono">{hits.length} hit{hits.length === 1 ? '' : 's'}</span>
        </div>
        <div className="divide-y divide-ink-800/60 max-h-[600px] overflow-y-auto">
          {hits.length === 0 && (
            <div className="p-6 text-center text-ink-500 text-xs italic">
              Run a query to see results.
            </div>
          )}
          {hits.map((h, i) => (
            <button key={`${h.id}-${i}`} onClick={() => onOpenRecord(h.id)}
              className="w-full text-left px-4 py-3 hover:bg-ink-800/40 transition-colors block">
              <div className="flex items-center gap-2 mb-1">
                <span className="font-mono text-[10px] text-ink-500 w-6 text-right">#{i + 1}</span>
                <span className="font-mono text-xs text-ink-200 truncate flex-1">{h.id}</span>
                <span className="tag-accent">d={h.distance?.toFixed(4) ?? '—'}</span>
              </div>
              <p className="text-xs text-ink-300 line-clamp-2 pl-8">
                {h.document || <span className="italic text-ink-600">No document text</span>}
              </p>
              {h.metadata && Object.keys(h.metadata).length > 0 && (
                <div className="pl-8 mt-1.5 flex flex-wrap gap-1">
                  {Object.entries(h.metadata).slice(0, 4).map(([k, v]) => (
                    <span key={k} className="tag"><span className="text-ink-500">{k}:</span>{String(v).slice(0, 30)}</span>
                  ))}
                </div>
              )}
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}

// =====================================================================
// Metrics Panel
// =====================================================================
function MetricsPanel({ name }) {
  const toast = useToast()
  const [data, setData] = useState(null)
  const [sample, setSample] = useState(200)
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const d = await api.metrics(name, sample)
      setData(d)
    } catch (e) { toast.error('Failed to load metrics', e.message) }
    finally { setLoading(false) }
  }, [name, sample, toast])

  useEffect(() => { load() }, [load])

  if (loading) return <div className="panel p-6 text-xs text-ink-400">Loading metrics…</div>
  if (!data) return null

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <label className="label">Sample size</label>
        <input type="number" value={sample} onChange={(e) => setSample(+e.target.value)}
          min={10} max={2000} className="input w-28" />
        <button onClick={load} className="btn"><RefreshCw size={11} /> Recompute</button>
        <div className="flex-1" />
        <span className="text-[10px] text-ink-500 font-mono">
          {data.sample_size.toLocaleString()} of {data.total.toLocaleString()} records sampled
        </span>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        <MetricCard title="Documents" rows={[
          ['With text', data.document_stats.with_documents],
          ['Without text', data.document_stats.without_documents],
          ['Min chars', data.document_stats.min_chars],
          ['Max chars', data.document_stats.max_chars],
          ['Avg chars', data.document_stats.avg_chars],
          ['Median chars', data.document_stats.median_chars],
        ]} />
        <MetricCard title="Embeddings" rows={[
          ['With embeddings', data.embedding_stats.with_embeddings ?? 0],
          ['Dimensions', (data.embedding_stats.dimensions || []).join(', ') || '—'],
          ['Min value', data.embedding_stats.min_value],
          ['Max value', data.embedding_stats.max_value],
          ['Mean', data.embedding_stats.mean_value],
          ['Stdev', data.embedding_stats.stdev_value],
        ]} />
        <div className="panel p-4">
          <div className="label mb-2">Metadata keys</div>
          <div className="space-y-1.5 text-xs max-h-72 overflow-y-auto">
            {Object.keys(data.metadata_keys).length === 0 && (
              <div className="text-ink-500 italic">None.</div>
            )}
            {Object.entries(data.metadata_keys).map(([k, info]) => (
              <div key={k} className="flex items-center gap-2">
                <span className="font-mono text-ink-200 flex-1 truncate">{k}</span>
                <div className="w-20 h-1.5 bg-ink-800 rounded overflow-hidden">
                  <div className="h-full bg-accent" style={{ width: `${info.coverage_pct}%` }} />
                </div>
                <span className="font-mono text-[10px] text-ink-400 w-12 text-right">
                  {info.coverage_pct}%
                </span>
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* Audit */}
      <div>
        <h3 className="font-display text-xl text-ink-50 mb-2">Quality audit</h3>
        <div className="space-y-2">
          {data.audit.map((a, i) => (
            <div key={i} className={`panel p-3 flex items-start gap-3 ${
              a.level === 'error' ? 'border-red-900/60' :
              a.level === 'warn' ? 'border-amber-900/60' :
              a.level === 'ok' ? 'border-accent/30' : ''
            }`}>
              <AlertTriangle size={14} className={`mt-0.5 ${
                a.level === 'error' ? 'text-red-400' :
                a.level === 'warn' ? 'text-amber-400' :
                a.level === 'ok' ? 'text-accent' : 'text-ink-400'
              }`} />
              <div className="flex-1">
                <div className="text-xs font-semibold text-ink-100">{a.title}</div>
                <div className="text-[11px] text-ink-400 mt-0.5">{a.detail}</div>
              </div>
              <span className={`tag ${
                a.level === 'error' ? 'border-red-900/60 text-red-300' :
                a.level === 'warn' ? 'border-amber-900/60 text-amber-300' :
                a.level === 'ok' ? 'border-accent/30 text-accent' : ''
              }`}>{a.level}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}

function MetricCard({ title, rows }) {
  return (
    <div className="panel p-4">
      <div className="label mb-3">{title}</div>
      <div className="space-y-1.5 text-xs">
        {rows.map(([k, v], i) => (
          <div key={i} className="flex justify-between gap-2">
            <span className="text-ink-400">{k}</span>
            <span className="font-mono text-ink-100 truncate">{v ?? '—'}</span>
          </div>
        ))}
      </div>
    </div>
  )
}

// =====================================================================
// Collection settings (metadata editor)
// =====================================================================
function CollectionSettings({ info, onSave }) {
  const [meta, setMeta] = useState('')
  useEffect(() => { if (info) setMeta(JSON.stringify(info.metadata || {}, null, 2)) }, [info])

  return (
    <div className="panel p-4 space-y-3 max-w-2xl">
      <div className="flex items-center gap-2">
        <Settings2 size={14} className="text-accent" />
        <h3 className="font-display text-xl text-ink-50">Collection metadata</h3>
      </div>
      <textarea value={meta} onChange={(e) => setMeta(e.target.value)}
        rows={10} className="input-mono" />
      <div className="flex justify-end">
        <button onClick={() => {
          try {
            const parsed = JSON.parse(meta)
            onSave(parsed)
          } catch { /* toast already by parent */ }
        }} className="btn-primary">Save</button>
      </div>
    </div>
  )
}

// =====================================================================
// Record editor modal (document / metadata / embedding)
// =====================================================================
function RecordEditor({ collectionName, recordId, onClose, onSaved, onDeleted }) {
  const toast = useToast()
  const [rec, setRec] = useState(null)
  const [loading, setLoading] = useState(true)
  const [doc, setDoc] = useState('')
  const [meta, setMeta] = useState('')
  const [emb, setEmb] = useState('')
  const [showVecViewer, setShowVecViewer] = useState(false)

  useEffect(() => {
    (async () => {
      try {
        const r = await api.getRecord(collectionName, recordId)
        setRec(r)
        setDoc(r.document || '')
        setMeta(JSON.stringify(r.metadata || {}, null, 2))
        setEmb(r.embedding ? JSON.stringify(r.embedding) : '')
      } catch (e) { toast.error('Failed to load record', e.message) }
      finally { setLoading(false) }
    })()
  }, [collectionName, recordId, toast])

  const save = async () => {
    try {
      const body = {}
      if (doc !== (rec.document || '')) body.document = doc
      const newMeta = JSON.parse(meta || '{}')
      if (JSON.stringify(newMeta) !== JSON.stringify(rec.metadata || {})) body.metadata = newMeta
      if (emb.trim()) {
        const parsed = JSON.parse(emb)
        if (Array.isArray(parsed) && JSON.stringify(parsed) !== JSON.stringify(rec.embedding || [])) {
          body.embedding = parsed
        }
      }
      if (Object.keys(body).length === 0) { toast.info('No changes'); return }
      await api.updateRecord(collectionName, recordId, body)
      toast.success('Record updated')
      onSaved()
    } catch (e) { toast.error('Save failed', e.message) }
  }

  const del = async () => {
    if (!confirm(`Delete record "${recordId}"?`)) return
    try {
      await api.bulkDelete(collectionName, { ids: [recordId] })
      toast.success('Record deleted')
      onDeleted()
    } catch (e) { toast.error('Delete failed', e.message) }
  }

  return (
    <Modal open={true} onClose={onClose}
      title="Record" subtitle={recordId} size="xl"
      footer={
        <>
          <button onClick={del} className="btn-danger"><Trash2 size={11} /> Delete</button>
          <div className="flex-1" />
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={save} className="btn-primary">Save changes</button>
        </>
      }>
      {loading ? <div className="text-xs text-ink-400">Loading…</div> : (
        <div className="space-y-4">
          <div>
            <label className="label block mb-1">Document</label>
            <textarea value={doc} onChange={(e) => setDoc(e.target.value)}
              rows={6} className="input" />
          </div>
          <div>
            <label className="label block mb-1">Metadata (JSON)</label>
            <textarea value={meta} onChange={(e) => setMeta(e.target.value)}
              rows={6} className="input-mono" />
          </div>
          <div>
            <div className="flex items-center justify-between mb-1">
              <label className="label">Embedding</label>
              <button onClick={() => setShowVecViewer(true)} className="btn-ghost"
                disabled={!rec?.embedding}>
                <Eye size={11} /> Open vector viewer
              </button>
            </div>
            <textarea value={emb} onChange={(e) => setEmb(e.target.value)}
              rows={4} className="input-mono"
              placeholder="[]" />
            <p className="text-[10px] text-ink-500 mt-1 font-mono">
              {rec?.embedding ? `${rec.embedding.length} dimensions` : 'No embedding'}
            </p>
          </div>
        </div>
      )}
      {showVecViewer && rec?.embedding && (
        <VectorViewer embedding={rec.embedding} onClose={() => setShowVecViewer(false)} />
      )}
    </Modal>
  )
}

// =====================================================================
// Vector viewer
// =====================================================================
function VectorViewer({ embedding, onClose }) {
  const min = Math.min(...embedding)
  const max = Math.max(...embedding)
  const range = max - min || 1
  return (
    <Modal open={true} onClose={onClose} title="Vector viewer"
      subtitle={`${embedding.length} dimensions · min ${min.toFixed(4)} · max ${max.toFixed(4)}`}
      size="xl"
      footer={<button onClick={onClose} className="btn">Close</button>}>
      <div className="space-y-3">
        {/* Visual bars */}
        <div className="panel-tight p-2 max-h-64 overflow-y-auto">
          <div className="flex items-end gap-px h-32">
            {embedding.slice(0, 256).map((v, i) => {
              const h = ((v - min) / range) * 100
              const positive = v >= 0
              return (
                <div key={i} className="flex-1 min-w-[2px] bg-ink-800 relative" style={{ height: '100%' }}>
                  <div
                    className={positive ? 'bg-accent absolute bottom-1/2 left-0 right-0' : 'bg-red-500 absolute top-1/2 left-0 right-0'}
                    style={{ height: `${Math.abs(h - 50)}%` }}
                  />
                </div>
              )
            })}
          </div>
          <div className="text-[10px] text-ink-500 mt-2 font-mono text-center">
            First {Math.min(256, embedding.length)} dimensions
          </div>
        </div>
        {/* Raw JSON */}
        <div>
          <div className="label mb-1">Raw values</div>
          <pre className="panel-tight p-3 text-[10px] font-mono text-ink-300 max-h-64 overflow-auto whitespace-pre-wrap break-all">
{JSON.stringify(embedding)}
          </pre>
        </div>
      </div>
    </Modal>
  )
}

// =====================================================================
// Add records modal
// =====================================================================
function AddRecordsModal({ open, onClose, collectionName, onSaved }) {
  const toast = useToast()
  const [mode, setMode] = useState('single') // single | json
  const [id, setId] = useState('')
  const [doc, setDoc] = useState('')
  const [metaTxt, setMetaTxt] = useState('')
  const [embTxt, setEmbTxt] = useState('')
  const [bulkTxt, setBulkTxt] = useState('')
  const [upsert, setUpsert] = useState(false)

  useEffect(() => {
    if (open) {
      setMode('single'); setId(''); setDoc(''); setMetaTxt(''); setEmbTxt(''); setBulkTxt(''); setUpsert(false)
    }
  }, [open])

  const submit = async () => {
    try {
      let body
      if (mode === 'single') {
        if (!id.trim()) { toast.warn('ID is required'); return }
        const meta = metaTxt.trim() ? JSON.parse(metaTxt) : null
        const emb = embTxt.trim() ? JSON.parse(embTxt) : null
        body = {
          ids: [id.trim()],
          documents: doc ? [doc] : null,
          metadatas: meta ? [meta] : null,
          embeddings: emb ? [emb] : null,
        }
      } else {
        const parsed = JSON.parse(bulkTxt)
        if (!Array.isArray(parsed)) { toast.warn('Bulk payload must be a JSON array of objects'); return }
        body = {
          ids: parsed.map((r) => r.id),
          documents: parsed.map((r) => r.document ?? null),
          metadatas: parsed.map((r) => r.metadata ?? null),
          embeddings: parsed.some((r) => r.embedding) ? parsed.map((r) => r.embedding ?? null) : null,
        }
      }
      const fn = upsert ? api.upsertRecords : api.addRecords
      await fn(collectionName, body)
      toast.success(upsert ? 'Upserted' : 'Added', `${body.ids.length} records`)
      onSaved()
    } catch (e) {
      toast.error('Failed to add records', e.message)
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Add records"
      subtitle="Provide IDs, documents, optional metadata, and optional explicit embeddings."
      size="lg"
      footer={
        <>
          <label className="flex items-center gap-1.5 text-xs text-ink-300 mr-auto">
            <input type="checkbox" checked={upsert} onChange={(e) => setUpsert(e.target.checked)}
              className="accent-accent" />
            Upsert (replace if ID exists)
          </label>
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={submit} className="btn-primary">{upsert ? 'Upsert' : 'Add'}</button>
        </>
      }>
      <div className="space-y-3">
        <div className="flex items-center gap-0 rounded-md border border-ink-700 overflow-hidden text-xs w-fit">
          <button onClick={() => setMode('single')}
            className={`px-3 py-1.5 ${mode === 'single' ? 'bg-accent/15 text-accent' : 'text-ink-400 hover:text-ink-100'}`}>
            Single record
          </button>
          <button onClick={() => setMode('json')}
            className={`px-3 py-1.5 ${mode === 'json' ? 'bg-accent/15 text-accent' : 'text-ink-400 hover:text-ink-100'}`}>
            Bulk JSON
          </button>
        </div>

        {mode === 'single' ? (
          <>
            <div>
              <label className="label block mb-1">ID</label>
              <input value={id} onChange={(e) => setId(e.target.value)} className="input-mono" placeholder="rec_001" />
            </div>
            <div>
              <label className="label block mb-1">Document</label>
              <textarea value={doc} onChange={(e) => setDoc(e.target.value)} rows={4} className="input" />
            </div>
            <div>
              <label className="label block mb-1">Metadata (JSON, optional)</label>
              <textarea value={metaTxt} onChange={(e) => setMetaTxt(e.target.value)} rows={3} className="input-mono" placeholder='{"category": "..."}' />
            </div>
            <div>
              <label className="label block mb-1">Embedding (JSON array, optional)</label>
              <textarea value={embTxt} onChange={(e) => setEmbTxt(e.target.value)} rows={3} className="input-mono" placeholder="Leave blank to auto-embed the document" />
            </div>
          </>
        ) : (
          <div>
            <label className="label block mb-1">Records (JSON array)</label>
            <textarea value={bulkTxt} onChange={(e) => setBulkTxt(e.target.value)}
              rows={14} className="input-mono"
              placeholder={`[
  {"id": "rec_001", "document": "hello", "metadata": {"k": "v"}},
  {"id": "rec_002", "document": "world", "embedding": [0.1, 0.2, ...]}
]`} />
          </div>
        )}
      </div>
    </Modal>
  )
}

// =====================================================================
// Simple modals
// =====================================================================
function RenameModal({ open, onClose, currentName, onRename }) {
  const [val, setVal] = useState('')
  useEffect(() => { if (open) setVal(currentName) }, [open, currentName])
  return (
    <Modal open={open} onClose={onClose} title="Rename collection"
      footer={
        <>
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={() => onRename(val)} disabled={!val || val === currentName} className="btn-primary">Rename</button>
        </>
      }>
      <input value={val} onChange={(e) => setVal(e.target.value)} className="input-mono" autoFocus />
    </Modal>
  )
}

function CloneModal({ open, onClose, currentName, onClone }) {
  const [val, setVal] = useState('')
  useEffect(() => { if (open) setVal(`${currentName}-copy`) }, [open, currentName])
  return (
    <Modal open={open} onClose={onClose} title="Clone collection"
      subtitle="Creates a new collection with the same metadata and copies all records."
      footer={
        <>
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={() => onClone(val)} disabled={!val || val === currentName} className="btn-primary">Clone</button>
        </>
      }>
      <input value={val} onChange={(e) => setVal(e.target.value)} className="input-mono" autoFocus />
    </Modal>
  )
}

function ConfirmModal({ open, onClose, title, body, confirmLabel = 'Confirm', onConfirm, danger }) {
  return (
    <Modal open={open} onClose={onClose} title={title}
      footer={
        <>
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={onConfirm} className={danger ? 'btn-danger' : 'btn-primary'}>{confirmLabel}</button>
        </>
      }>
      <p className="text-sm text-ink-300">{body}</p>
    </Modal>
  )
}

function BulkMetadataModal({ open, onClose, count, onApply }) {
  const [txt, setTxt] = useState('')
  useEffect(() => { if (open) setTxt('') }, [open])
  return (
    <Modal open={open} onClose={onClose} title={`Patch metadata on ${count} records`}
      subtitle="Keys in the patch are merged with each record's existing metadata."
      footer={
        <>
          <button onClick={onClose} className="btn">Cancel</button>
          <button onClick={() => {
            try { onApply(JSON.parse(txt)) }
            catch { /* ignore */ }
          }} className="btn-primary">Apply</button>
        </>
      }>
      <textarea value={txt} onChange={(e) => setTxt(e.target.value)} rows={8}
        className="input-mono" placeholder='{"reviewed": true, "batch": "2025-q1"}' />
    </Modal>
  )
}
