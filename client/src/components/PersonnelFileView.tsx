import { useState, useEffect, useCallback } from 'react'
import type { AuthState, Personnel, PersonnelFile } from '../types'
import { RELATION_TYPE_LABELS } from '../types'
import { getPersonnelFiles } from '../api'
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
      const data = await getPersonnelFiles(personnel.personnelId, auth.token)
      setFiles(data)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Yükleme hatası')
    } finally {
      setLoading(false)
    }
  }, [personnel.personnelId, auth.token])

  useEffect(() => { loadFiles() }, [loadFiles])

  const grouped = groupByType(files)

  return (
    <div>
      {/* Personel header */}
      <div className="flex items-start justify-between mb-6">
        <div className="flex items-center gap-3">
          <button
            onClick={onBack}
            className="p-1.5 rounded-lg text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-colors sm:hidden"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
            </svg>
          </button>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">{personnel.displayName}</h2>
            <p className="text-sm text-gray-500">
              {[personnel.title, personnel.department].filter(Boolean).join(' · ')}
              <span className="ml-2 text-xs font-mono text-gray-400">{personnel.personnelId}</span>
            </p>
          </div>
        </div>

        {writable && (
          <button
            onClick={() => setShowUpload(true)}
            className="flex items-center gap-1.5 px-4 py-2 bg-brand-600 hover:bg-brand-700 text-white text-sm font-medium rounded-xl transition-colors"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-8l-4-4m0 0L8 8m4-4v12" />
            </svg>
            Dosya Yükle
          </button>
        )}
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
                    personnelId={personnel.personnelId}
                    auth={auth}
                    writable={writable}
                    onArchived={loadFiles}
                  />
                ))}
              </div>
            </section>
          ))}
        </div>
      )}

      {showUpload && (
        <UploadModal
          personnel={personnel}
          auth={auth}
          onClose={() => setShowUpload(false)}
          onUploaded={() => { setShowUpload(false); loadFiles() }}
        />
      )}
    </div>
  )
}
