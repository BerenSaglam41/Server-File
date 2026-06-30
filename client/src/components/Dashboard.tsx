import { useState, useEffect, useCallback } from 'react'
import type { AuthState, Personnel } from '../types'
import { searchPersonnel } from '../api'
import PersonnelCard from './PersonnelCard'
import PersonnelFileView from './PersonnelFileView'

interface Props {
  auth: AuthState
  onLogout: () => void
}

export default function Dashboard({ auth, onLogout }: Props) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<Personnel[]>([])
  const [searching, setSearching] = useState(false)
  const [selected, setSelected] = useState<Personnel | null>(null)
  const [searchError, setSearchError] = useState('')

  const runSearch = useCallback(async (q: string) => {
    setSearching(true)
    setSearchError('')
    try {
      const data = await searchPersonnel(q, auth.token)
      setResults(data)
    } catch (err) {
      setSearchError(err instanceof Error ? err.message : 'Arama hatası')
    } finally {
      setSearching(false)
    }
  }, [auth.token])

  // İlk açılışta tüm personeli yükle
  useEffect(() => { runSearch('') }, [runSearch])

  useEffect(() => {
    const t = setTimeout(() => runSearch(query), 300)
    return () => clearTimeout(t)
  }, [query, runSearch])

  const roleLabel = auth.user.roles.includes('personnel.files.read.all')
    ? 'İK Yöneticisi'
    : auth.user.roles.includes('personnel.files.read.team')
    ? 'Ekip Yöneticisi'
    : 'Personel'

  return (
    <div className="min-h-screen flex flex-col">
      {/* Header */}
      <header className="bg-white border-b border-gray-200 shadow-sm sticky top-0 z-10">
        <div className="max-w-6xl mx-auto px-4 h-14 flex items-center gap-4">
          <div className="flex items-center gap-2 mr-4">
            <div className="w-7 h-7 rounded-lg bg-brand-600 flex items-center justify-center">
              <svg className="w-4 h-4 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                  d="M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8" />
              </svg>
            </div>
            <span className="font-semibold text-gray-900 text-sm hidden sm:block">Personel Dosya</span>
          </div>

          {/* Search */}
          <div className="flex-1 relative max-w-md">
            <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
            <input
              type="text"
              value={query}
              onChange={e => setQuery(e.target.value)}
              placeholder="İsim veya ID ile ara…"
              className="w-full pl-9 pr-3 py-1.5 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-brand-500 focus:border-transparent"
            />
            {searching && (
              <div className="absolute right-3 top-1/2 -translate-y-1/2 w-3.5 h-3.5 border-2 border-brand-500 border-t-transparent rounded-full animate-spin" />
            )}
          </div>

          {/* User */}
          <div className="flex items-center gap-2 ml-auto">
            <div className="text-right hidden sm:block">
              <p className="text-xs font-medium text-gray-900">{auth.user.preferred_username}</p>
              <p className="text-xs text-gray-500">{roleLabel}</p>
            </div>
            <button
              onClick={onLogout}
              className="p-1.5 rounded-lg text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-colors"
              title="Çıkış Yap"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                  d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
              </svg>
            </button>
          </div>
        </div>
      </header>

      <div className="flex flex-1 max-w-6xl mx-auto w-full px-4 py-6 gap-6">
        {/* Sidebar: personnel list */}
        <aside className="w-72 flex-shrink-0">
          <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-3">
            Personel {results.length > 0 && `(${results.length})`}
          </p>

          {searchError && (
            <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg px-3 py-2 mb-3">
              {searchError}
            </div>
          )}

          <div className="space-y-2">
            {results.map(p => (
              <PersonnelCard
                key={p.personnelId}
                personnel={p}
                selected={selected?.personnelId === p.personnelId}
                onClick={() => setSelected(p)}
              />
            ))}
            {!searching && results.length === 0 && (
              <p className="text-sm text-gray-400 text-center py-8">
                {query ? 'Sonuç bulunamadı' : 'Personel yok'}
              </p>
            )}
          </div>
        </aside>

        {/* Main: file view */}
        <main className="flex-1 min-w-0">
          {selected ? (
            <PersonnelFileView
              personnel={selected}
              auth={auth}
              onBack={() => setSelected(null)}
            />
          ) : (
            <div className="flex flex-col items-center justify-center h-64 text-gray-400">
              <svg className="w-12 h-12 mb-3 opacity-30" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5}
                  d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              <p className="text-sm">Soldaki listeden bir personel seçin</p>
            </div>
          )}
        </main>
      </div>
    </div>
  )
}
