import { X } from 'lucide-react'
import { useEffect } from 'react'

export default function Modal({ open, title, subtitle, onClose, children, footer, size = 'md' }) {
  useEffect(() => {
    if (!open) return
    const onKey = (e) => { if (e.key === 'Escape') onClose?.() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  if (!open) return null
  const sizes = {
    sm: 'max-w-md',
    md: 'max-w-2xl',
    lg: 'max-w-4xl',
    xl: 'max-w-6xl',
  }
  return (
    <div
      className="fixed inset-0 z-40 flex items-center justify-center p-4 bg-ink-950/80 backdrop-blur-sm"
      onClick={onClose}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className={`panel w-full ${sizes[size]} max-h-[90vh] flex flex-col shadow-2xl slide-up`}
      >
        <div className="flex items-start justify-between border-b border-ink-700/60 px-5 py-3">
          <div>
            <h3 className="font-display text-xl text-ink-50">{title}</h3>
            {subtitle && <p className="text-xs text-ink-400 mt-0.5">{subtitle}</p>}
          </div>
          <button onClick={onClose} className="text-ink-400 hover:text-ink-100 p-1">
            <X size={16} />
          </button>
        </div>
        <div className="flex-1 overflow-auto p-5">{children}</div>
        {footer && (
          <div className="border-t border-ink-700/60 px-5 py-3 flex items-center justify-end gap-2 bg-ink-950/40">
            {footer}
          </div>
        )}
      </div>
    </div>
  )
}
