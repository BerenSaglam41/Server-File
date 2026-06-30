import { useState } from 'react'
import type { PersonnelFile } from '../types'
import { RELATION_TYPE_LABELS } from '../types'

interface Props {
  file: PersonnelFile
  writable: boolean
  onArchived: () => void
  onDownload?: (() => Promise<{ blob: Blob; contentType: string; fileName: string }>) | null
  onArchive?: (() => Promise<void>) | null
}

function formatSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function formatDate(iso: string) {
  return new Date(iso).toLocaleDateString('tr-TR', {
    day: '2-digit', month: 'short', year: 'numeric'
  })
}

function fileIcon(contentType: string) {
  if (contentType.startsWith('image/')) return 'IMG'
  if (contentType === 'application/pdf') return 'PDF'
  return 'FILE'
}

export default function FileCard({ file, writable, onArchived, onDownload, onArchive }: Props) {
  const [archiving, setArchiving] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [error, setError] = useState('')
  const displayName = file.originalFileName || `${RELATION_TYPE_LABELS[file.relationType] ?? file.relationType}.${file.extension}`

  async function handleDownload() {
    if (!onDownload) return
    setDownloading(true)
    setError('')
    try {
      const { blob, fileName } = await onDownload()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = fileName || displayName
      a.click()
      URL.revokeObjectURL(url)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'İndirme hatası')
    } finally {
      setDownloading(false)
    }
  }

  async function handleArchive() {
    if (!onArchive) return
    if (!confirm(`"${displayName}" dosyasını arşivlemek istiyor musunuz?`)) return
    setArchiving(true)
    setError('')
    try {
      await onArchive()
      onArchived()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Arşivleme hatası')
      setArchiving(false)
    }
  }

  return (
    <div className="bg-white border border-gray-200 rounded-xl p-3.5 hover:border-gray-300 transition-colors">
      <div className="flex items-start gap-3">
        <div className="w-10 h-10 rounded-lg bg-gray-100 text-gray-600 flex-shrink-0 mt-0.5 flex items-center justify-center text-[10px] font-semibold">
          {fileIcon(file.contentType)}
        </div>
        <div className="flex-1 min-w-0">
          <p className="text-sm font-medium text-gray-900 truncate" title={displayName}>
            {displayName}
          </p>
          <p className="text-xs text-gray-500 mt-0.5">
            {formatSize(file.sizeBytes)} · {formatDate(file.createdAt)}
          </p>
          {error && (
            <p className="text-xs text-red-600 mt-1">{error}</p>
          )}
        </div>
        <div className="flex items-center gap-1 flex-shrink-0">
          {/* Download */}
          {onDownload && (
            <button
              onClick={handleDownload}
              disabled={downloading}
              className="p-1.5 rounded-lg text-gray-400 hover:text-brand-600 hover:bg-brand-50 transition-colors disabled:opacity-40"
              title="İndir"
            >
              {downloading ? (
                <div className="w-3.5 h-3.5 border-2 border-brand-500 border-t-transparent rounded-full animate-spin" />
              ) : (
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                    d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                </svg>
              )}
            </button>
          )}

          {/* Archive */}
          {writable && onArchive && (
            <button
              onClick={handleArchive}
              disabled={archiving}
              className="p-1.5 rounded-lg text-gray-400 hover:text-red-600 hover:bg-red-50 transition-colors disabled:opacity-40"
              title="Arşivle"
            >
              {archiving ? (
                <div className="w-3.5 h-3.5 border-2 border-red-400 border-t-transparent rounded-full animate-spin" />
              ) : (
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                    d="M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4" />
                </svg>
              )}
            </button>
          )}
        </div>
      </div>
    </div>
  )
}
