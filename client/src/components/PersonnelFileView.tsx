import { useState, useEffect, useCallback } from 'react'
import type { AuthState, Personnel, PersonnelFile } from '../types'
import { RELATION_TYPE_LABELS, SINGLE_PRIMARY_TYPES, UPLOAD_RELATION_TYPES } from '../types'
import { getPersonnelFiles, uploadFile, archiveFile, archiveSinglePrimary, createDownloadTicket } from '../api'
import { canWrite } from '../auth'
import FileCard from './FileCard'
import UploadModal from './UploadModal'

interface Props {
  personnel: Personnel
  auth: AuthState
  onBack: () => void
}

function groupByType(files: PersonnelFile[]): Map<string, PersonnelFile[]> {
  const map = new Map<string, PersonnelFile[]>()
  for (const f of files) {
    if (!map.has(f.relationType)) map.set(f.relationType, [])
    map.get(f.relationType)!.push(f)
  }
  return map
}

export default function PersonnelFileView({ personnel, auth, onBack }: Props) {
  const [files, setFiles] = useState<PersonnelFile[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [showUpload, setShowUpload] = useState(false)

  const writable = canWrite(auth, personnel.personnelId)

  const loadFiles = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const data = await getPersonnelFiles(personnel.personnelId)
      setFiles(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Yükleme hatası')
    } finally {
      setLoading(false)
    }
  }, [personnel.personnelId])

  useEffect(() => { loadFiles() }, [loadFiles])

  const grouped = groupByType(files)

  return (
    <div>
      {/* Personel header */}
      <div className="flex items-start justify-between mb-5 gap-2">
        <div className="flex items-center gap-2 min-w-0">
          {/* Geri — sadece mobilde görünür */}
          <button
            onClick={onBack}
            className="p-1.5 rounded-lg text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-colors sm:hidden flex-shrink-0"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
            </svg>
          </button>
          <div className="min-w-0">
            <h2 className="text-lg font-semibold text-gray-900 truncate">{personnel.displayName}</h2>
            <p className="text-xs text-gray-500 truncate">
              {[personnel.title, personnel.department].filter(Boolean).join(' · ')}
              <span className="ml-1.5 font-mono text-gray-400">{personnel.personnelId}</span>
            </p>
          </div>
        </div>

        <div className="flex items-center gap-1.5 flex-shrink-0">
          {/* Refresh */}
          <button
            onClick={loadFiles}
            disabled={loading}
            className="p-2 rounded-xl text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-colors disabled:opacity-40"
            title="Yenile"
          >
            <svg
              className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`}
              fill="none" viewBox="0 0 24 24" stroke="currentColor"
            >
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
          </button>

          {writable && (
            <button
              onClick={() => setShowUpload(true)}
              className="flex items-center gap-1.5 px-3 py-2 bg-brand-600 hover:bg-brand-700 text-white text-sm font-medium rounded-xl transition-colors"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-8l-4-4m0 0L8 8m4-4v12" />
              </svg>
              <span className="hidden sm:inline">Dosya Yükle</span>
              <span className="sm:hidden">Yükle</span>
            </button>
          )}
        </div>
      </div>

      {/* Content */}
      {loading && (
        <div className="flex items-center justify-center py-16">
          <div className="w-6 h-6 border-2 border-brand-500 border-t-transparent rounded-full animate-spin" />
        </div>
      )}

      {error && !loading && (
        <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-xl px-4 py-3">
          {error}
        </div>
      )}

      {!loading && !error && files.length === 0 && (
        <div className="flex flex-col items-center justify-center py-16 text-gray-400">
          <svg className="w-10 h-10 mb-2 opacity-30" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5}
              d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
          <p className="text-sm">Henüz dosya yok</p>
          {writable && (
            <button
              onClick={() => setShowUpload(true)}
              className="mt-3 text-sm text-brand-600 hover:underline"
            >
              İlk dosyayı yükle
            </button>
          )}
        </div>
      )}

      {!loading && files.length > 0 && (
        <div className="space-y-6">
          {Array.from(grouped.entries()).map(([type, typeFiles]) => (
            <section key={type}>
              <div className="flex items-center gap-2 mb-3">
                <h3 className="text-sm font-semibold text-gray-700">
                  {RELATION_TYPE_LABELS[type] ?? type}
                </h3>
                {typeFiles.length > 1 && (
                  <span className="text-xs bg-gray-100 text-gray-500 px-2 py-0.5 rounded-full">
                    {typeFiles.length}
                  </span>
                )}
              </div>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                {typeFiles.map(f => (
                  <FileCard
                    key={f.fileId}
                    file={f}
                    writable={writable}
                    onArchived={loadFiles}
                    onDownload={async () => {
                      const { downloadUrl } = await createDownloadTicket(personnel.personnelId, f.fileId)
                      return { url: downloadUrl }
                    }}
                    onArchive={
                      SINGLE_PRIMARY_TYPES.has(f.relationType)
                        ? () => archiveSinglePrimary(personnel.personnelId, f.relationType)
                        : () => archiveFile(personnel.personnelId, f.fileId)
                    }
                  />
                ))}
              </div>
            </section>
          ))}
        </div>
      )}

      {showUpload && (
        <UploadModal
          entityDisplayName={personnel.displayName}
          relationTypes={UPLOAD_RELATION_TYPES}
          uploadFn={(rt, file, onProgress) => uploadFile(personnel.personnelId, rt, file, onProgress)}
          onClose={() => setShowUpload(false)}
          onUploaded={() => { setShowUpload(false); loadFiles() }}
        />
      )}
    </div>
  )
}
