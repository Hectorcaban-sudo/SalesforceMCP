import { createContext, useContext, useState, useCallback } from 'react'
import { Check, AlertTriangle, X, Info } from 'lucide-react'

const ToastCtx = createContext(null)

export function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([])

  const push = useCallback((t) => {
    const id = Math.random().toString(36).slice(2)
    const toast = { id, kind: 'info', duration: 3500, ...t }
    setToasts((prev) => [...prev, toast])
    setTimeout(() => {
      setToasts((prev) => prev.filter((x) => x.id !== id))
    }, toast.duration)
  }, [])

  const value = {
    success: (msg, detail) => push({ kind: 'success', msg, detail }),
    error: (msg, detail) => push({ kind: 'error', msg, detail, duration: 6000 }),
    info: (msg, detail) => push({ kind: 'info', msg, detail }),
    warn: (msg, detail) => push({ kind: 'warn', msg, detail }),
  }

  return (
    <ToastCtx.Provider value={value}>
      {children}
      <div className="fixed bottom-4 right-4 z-50 flex flex-col gap-2 max-w-sm">
        {toasts.map((t) => (
          <ToastItem key={t.id} t={t} onClose={() => setToasts((p) => p.filter(x => x.id !== t.id))} />
        ))}
      </div>
    </ToastCtx.Provider>
  )
}

function ToastItem({ t, onClose }) {
  const Icon =
    t.kind === 'success' ? Check :
    t.kind === 'error' ? AlertTriangle :
    t.kind === 'warn' ? AlertTriangle : Info
  const color =
    t.kind === 'success' ? 'text-accent border-accent/30 bg-accent/5' :
    t.kind === 'error' ? 'text-red-300 border-red-900/60 bg-red-950/40' :
    t.kind === 'warn' ? 'text-amber-300 border-amber-900/60 bg-amber-950/40' :
    'text-ink-200 border-ink-700 bg-ink-900'
  return (
    <div className={`slide-up panel-tight ${color} border px-3 py-2 flex items-start gap-2 shadow-xl`}>
      <Icon size={14} className="mt-0.5 shrink-0" />
      <div className="flex-1 min-w-0">
        <div className="text-xs font-semibold">{t.msg}</div>
        {t.detail && <div className="text-[11px] opacity-80 mt-0.5 break-words">{t.detail}</div>}
      </div>
      <button onClick={onClose} className="text-ink-400 hover:text-ink-100">
        <X size={12} />
      </button>
    </div>
  )
}

export function useToast() {
  const ctx = useContext(ToastCtx)
  if (!ctx) throw new Error('useToast must be inside ToastProvider')
  return ctx
}
