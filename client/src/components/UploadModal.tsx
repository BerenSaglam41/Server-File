import { useState, useRef, FormEvent } from 'react'
import type { AuthState, Personnel, UploadRelationType } from '../types'
import { UPLOAD_RELATION_TYPES } from '../types'
import { uploadFile } from '../api'

interface Props {
  personnel: Personnel
  auth: AuthState
  onClose: () => void
  onUploaded: () => void
}

export default function UploadModal({ personnel, auth, onClose, onUploaded }: Props) {
  const [relationType, setRelationType] = useState<UploadRelationType>(UPLOAD_RELATION_TYPES[0].value)
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (!file) return
    setUploading(true)
    setError('')
    try {
      await uploadFile(personnel.personnelId, relationType, file, auth.token)
      onUploaded()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Yükleme hatası')
      setUploading(false)
    }
  }

  function handleDrop(e: React.DragEvent) {
    e.preventDefault()
    const f = e.dataTransfer.files[0]
    if (f) setFile(f)
  }

  return (
    <div
      className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4"
      onClick={(e) => { if (e.target === e.currentTarget) onClose() }}
    >
      <div className="bg-white rounded-2xl shadow-xl w-full max-w-md">
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-100">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Dosya Yükle</h2>
            <p className="text-xs text-gray-500">{personnel.displayName}</p>
          </div>
          <button
            onClick={onClose}
            className="p-1.5 rounded-lg text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-colors"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4">
          {/* Relation type */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1.5">Dosya Türü</label>
            <select
              value={relationType}
              onChange={e => setRelationType(e.target.value as UploadRelationType)}
              className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-brand-500 focus:border-transparent bg-white"
            >
              {UPLOAD_RELATION_TYPES.map(rt => (
                <option key={rt.value} value={rt.value}>{rt.label}</option>
              ))}
            </select>
          </div>

          {/* File drop zone */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1.5">Dosya</label>
            <div
              onDrop={handleDrop}
              onDragOver={e => e.preventDefault()}
              onClick={() => fileRef.current?.click()}
              className={`border-2 border-dashed rounded-xl p-6 text-center cursor-pointer transition-colors ${
                file
                  ? 'border-brand-300 bg-brand-50'
                  : 'border-gray-200 hover:border-brand-300 hover:bg-gray-50'
              }`}
            >
              <input
                ref={fileRef}
                type="file"
                className="hidden"
                onChange={e => setFile(e.target.files?.[0] ?? null)}
              />
              {file ? (
                <div className="flex items-center justify-center gap-2">
                  <svg className="w-5 h-5 text-brand-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                      d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <span className="text-sm text-gray-900 font-medium truncate max-w-xs">{file.name}</span>
                  <button
                    type="button"
                    onClick={e => { e.stopPropagation(); setFile(null) }}
                    className="text-gray-400 hover:text-gray-600"
                  >
                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                  </button>
                </div>
              ) : (
                <div>
                  <svg className="mx-auto w-8 h-8 text-gray-300 mb-2" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5}
                      d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-8l-4-4m0 0L8 8m4-4v12" />
                  </svg>
                  <p className="text-sm text-gray-500">Dosyayı buraya sürükleyin</p>
                  <p className="text-xs text-gray-400 mt-0.5">veya seçmek için tıklayın</p>
                </div>
              )}
            </div>
          </div>

          {error && (
            <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg px-3 py-2">
              {error}
            </div>
          )}

          <div className="flex gap-3 pt-1">
            <button
              type="button"
              onClick={onClose}
              className="flex-1 py-2 border border-gray-300 rounded-xl text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
            >
              İptal
            </button>
            <button
              type="submit"
              disabled={!file || uploading}
              className="flex-1 py-2 bg-brand-600 hover:bg-brand-700 disabled:opacity-50 text-white text-sm font-medium rounded-xl transition-colors flex items-center justify-center gap-1.5"
            >
              {uploading ? (
                <>
                  <div className="w-3.5 h-3.5 border-2 border-white border-t-transparent rounded-full animate-spin" />
                  Yükleniyor…
                </>
              ) : (
                'Yükle'
              )}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
