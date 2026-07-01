import { useState, useEffect, useCallback } from 'react'
import type { AuthState, OpsHealth, OpsServices, OpsDisk, OpsAlerts, OpsBackups, OpsVersion, OpsDashboard } from '../types'
import { bffRefresh } from '../api'

interface Props {
  auth: AuthState
  onBack?: () => void
  onLogout: () => void
}

interface Slot<T> { data: T | null; err: string | null }

async function opsFetch<T>(path: string): Promise<T> {
  const res = await fetch(path, { credentials: 'include' })
  if (res.status === 401) {
    await bffRefresh()
    const retry = await fetch(path, { credentials: 'include' })
    if (!retry.ok) throw new Error(`HTTP ${retry.status}`)
    return retry.json()
  }
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json()
}

function wait(ms: number) {
  return new Promise(resolve => window.setTimeout(resolve, ms))
}

function Dot({ state }: { state: string }) {
  const cls =
    state === 'healthy' || state === 'running' || state === 'clean' || state === 'success'
      ? 'bg-green-400'
      : state === 'degraded' || state === 'warning' || state === 'paused'
      ? 'bg-yellow-400'
      : state === 'unhealthy' || state === 'critical' || state === 'exited' || state === 'failed' || state === 'error'
      ? 'bg-red-400'
      : 'bg-gray-500'
  return <span className={`inline-block w-2 h-2 rounded-full flex-shrink-0 ${cls}`} />
}

function Card({ children, wide }: { children: React.ReactNode; wide?: boolean }) {
  return (
    <div className={`bg-gray-800 border border-gray-700 rounded-xl p-4 ${wide ? 'md:col-span-2' : ''}`}>
      {children}
    </div>
  )
}

function Label({ children }: { children: React.ReactNode }) {
  return <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">{children}</p>
}

function ErrText({ msg }: { msg: string | null }) {
  if (!msg) return null
  return <p className="text-xs text-red-400">{msg}</p>
}

function Loading() {
  return <p className="text-xs text-gray-600">Yükleniyor…</p>
}

function formatDate(value: string | null | undefined) {
  if (!value) return '—'
  const compact = value.match(/^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/)
  if (compact) return formatDate(`${compact[1]}-${compact[2]}-${compact[3]}T${compact[4]}:${compact[5]}:${compact[6]}Z`)
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return value
  return d.toLocaleString('tr-TR', { dateStyle: 'medium', timeStyle: 'short' })
}

function formatBackupDate(value: string) {
  const m = value.match(/^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/)
  if (!m) return value
  return formatDate(`${m[1]}-${m[2]}-${m[3]}T${m[4]}:${m[5]}:${m[6]}Z`)
}

function formatDuration(seconds: number | null | undefined) {
  if (seconds == null) return '—'
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const mins = Math.floor((seconds % 3600) / 60)
  if (days > 0) return `${days} gün ${hours} sa`
  if (hours > 0) return `${hours} sa ${mins} dk`
  return `${Math.max(1, mins)} dk`
}

function serviceAgeSeconds(startedAt: string | null | undefined, fallback: number | null | undefined) {
  if (!startedAt) return fallback
  const started = new Date(startedAt).getTime()
  if (Number.isNaN(started)) return fallback
  return Math.max(0, Math.floor((Date.now() - started) / 1000))
}

export default function OpsConsole({ auth, onBack, onLogout }: Props) {
  const [health,   setHealth]   = useState<Slot<OpsHealth>>  ({ data: null, err: null })
  const [services, setServices] = useState<Slot<OpsServices>>({ data: null, err: null })
  const [disk,     setDisk]     = useState<Slot<OpsDisk>>    ({ data: null, err: null })
  const [alerts,   setAlerts]   = useState<Slot<OpsAlerts>>  ({ data: null, err: null })
  const [backups,  setBackups]  = useState<Slot<OpsBackups>> ({ data: null, err: null })
  const [version,  setVersion]  = useState<Slot<OpsVersion>> ({ data: null, err: null })
  const [spinning, setSpinning] = useState(true)
  const [lastAt,   setLastAt]   = useState('')
  const [banner,   setBanner]   = useState('')

  const refresh = useCallback(async () => {
    setSpinning(true)
    try {
      let d: OpsDashboard
      try {
        d = await opsFetch<OpsDashboard>('/ops/dashboard')
      } catch {
        await wait(800)
        d = await opsFetch<OpsDashboard>('/ops/dashboard')
      }
      setHealth({ data: d.health, err: null })
      setServices({ data: d.services, err: null })
      setDisk({ data: d.disk, err: null })
      setAlerts({ data: d.alerts, err: null })
      setBackups({ data: d.backups, err: null })
      setVersion({ data: d.version, err: null })
      setLastAt(new Date(d.timestamp).toLocaleTimeString('tr-TR'))
      setBanner('')
    } catch {
      const err = 'Veri alınamadı'
      setBanner('Son yenileme başarısız; mevcut veriler korunuyor.')
      setHealth(prev => ({ data: prev.data, err: prev.data ? null : err }))
      setServices(prev => ({ data: prev.data, err: prev.data ? null : err }))
      setDisk(prev => ({ data: prev.data, err: prev.data ? null : err }))
      setAlerts(prev => ({ data: prev.data, err: prev.data ? null : err }))
      setBackups(prev => ({ data: prev.data, err: prev.data ? null : err }))
      setVersion(prev => ({ data: prev.data, err: prev.data ? null : err }))
    } finally {
      setSpinning(false)
    }
  }, [])

  useEffect(() => {
    refresh()
    const id = setInterval(refresh, 30_000)
    return () => clearInterval(id)
  }, [refresh])

  return (
    <div className="min-h-screen bg-gray-900 text-gray-100 flex flex-col">

      {/* Header */}
      <header className="bg-gray-950 border-b border-gray-800 sticky top-0 z-10">
        <div className="max-w-6xl mx-auto px-4 h-14 flex items-center gap-3">
          {onBack && (
            <button
              onClick={onBack}
              className="p-1.5 rounded-lg text-gray-400 hover:text-gray-200 hover:bg-gray-800 transition-colors"
              title="Geri Dön"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
              </svg>
            </button>
          )}
          <div className="w-7 h-7 rounded-lg bg-indigo-600 flex items-center justify-center flex-shrink-0">
            <svg className="w-4 h-4 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M9 3H5a2 2 0 00-2 2v4m6-6h10a2 2 0 012 2v4M9 3v18m0 0h10a2 2 0 002-2V9M9 21H5a2 2 0 01-2-2V9m0 0h18" />
            </svg>
          </div>
          <span className="font-semibold text-gray-100 text-sm">Ops Konsolu</span>
          <div className="flex-1" />
          {lastAt && <span className="text-xs text-gray-500 hidden sm:block">Son: {lastAt}</span>}
          <button
            onClick={refresh}
            disabled={spinning}
            className="p-1.5 rounded-lg text-gray-400 hover:text-gray-200 hover:bg-gray-800 transition-colors disabled:opacity-40"
            title="Yenile (otomatik 30s)"
          >
            <svg className={`w-4 h-4 ${spinning ? 'animate-spin' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            <span className="sr-only">Yenile</span>
          </button>
          <div className="hidden sm:block text-right ml-2">
            <p className="text-xs font-medium text-gray-200">{auth.user.preferred_username}</p>
            <p className="text-xs text-gray-500">Ops</p>
          </div>
          <button
            onClick={onLogout}
            className="p-1.5 rounded-lg text-gray-400 hover:text-gray-200 hover:bg-gray-800 transition-colors"
            title="Çıkış Yap"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
            </svg>
            <span className="sr-only">Çıkış Yap</span>
          </button>
        </div>
      </header>

      {/* Grid */}
      <div className="flex-1 max-w-6xl mx-auto w-full px-4 py-6">
        {banner && (
          <div className="mb-4 rounded-lg border border-yellow-600/40 bg-yellow-500/10 px-3 py-2 text-xs text-yellow-200">
            {banner}
          </div>
        )}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">

          {/* Servis Durumu */}
          <Card>
            <Label>Servis Durumu</Label>
            {health.err ? <ErrText msg={health.err} /> : health.data ? (
              <div className="space-y-2">
                <div className="flex items-center gap-2 mb-3">
                  <Dot state={health.data.status} />
                  <span className="text-xs text-gray-400">Genel: {health.data.status}</span>
                </div>
                {health.data.services.map(s => (
                  <div key={s.name} className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <Dot state={s.status} />
                      <span className="text-sm text-gray-200">{s.name}</span>
                    </div>
                    <span className="text-xs text-gray-500">
                      {s.latency_ms != null ? `${s.latency_ms}ms` : s.reason ?? '—'}
                    </span>
                  </div>
                ))}
              </div>
            ) : <Loading />}
          </Card>

          {/* Disk */}
          <Card>
            <Label>Disk Kullanımı</Label>
            {disk.err ? <ErrText msg={disk.err} /> : disk.data ? (
              <div className="space-y-4">
                {[
                  { label: 'API Sunucu', pct: disk.data.api_server_pct },
                  { label: 'Files-01 (/mnt)', pct: disk.data.files01_pct },
                ].map(({ label, pct }) => (
                  <div key={label}>
                    <div className="flex justify-between text-xs mb-1.5">
                      <span className="text-gray-400">{label}</span>
                      <span className={
                        pct != null && pct >= 90 ? 'text-red-400' :
                        pct != null && pct >= 80 ? 'text-yellow-400' : 'text-green-400'
                      }>{pct != null ? `${pct}%` : '—'}</span>
                    </div>
                    <div className="h-1.5 bg-gray-700 rounded-full overflow-hidden">
                      <div
                        className={`h-full rounded-full transition-all ${
                          pct != null && pct >= 90 ? 'bg-red-500' :
                          pct != null && pct >= 80 ? 'bg-yellow-500' : 'bg-green-500'
                        }`}
                        style={{ width: `${pct ?? 0}%` }}
                      />
                    </div>
                  </div>
                ))}
                {disk.data.status !== 'unknown' && (
                  <div className="flex items-center gap-2 mt-2 pt-2 border-t border-gray-700">
                    <Dot state={disk.data.status} />
                    <span className="text-xs text-gray-500">{disk.data.reason ?? disk.data.status}</span>
                  </div>
                )}
              </div>
            ) : <Loading />}
          </Card>

          {/* Uyarılar */}
          <Card>
            <Label>Uyarılar</Label>
            {alerts.err ? <ErrText msg={alerts.err} /> : alerts.data ? (
              alerts.data.count === 0 ? (
                <div className="flex items-center gap-2 text-sm text-green-400">
                  <Dot state="healthy" />
                  Aktif uyarı yok
                </div>
              ) : (
                <div className="space-y-2">
                  {alerts.data.alerts.map((a, i) => (
                    <div key={i} className="flex items-start gap-2">
                      <Dot state={a.severity} />
                      <div>
                        <span className="text-sm text-gray-200 font-medium">{a.source}</span>
                        <p className="text-xs text-gray-500 mt-0.5">{a.reason}</p>
                      </div>
                    </div>
                  ))}
                </div>
              )
            ) : <Loading />}
          </Card>

          {/* Versiyon */}
          <Card>
            <Label>Versiyon</Label>
            {version.err ? <ErrText msg={version.err} /> : version.data ? (
              <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-xs">
                {([
                  ['Servis',     version.data.service],
                  ['Versiyon',   version.data.version],
                  ['Ortam',      version.data.environment],
                  ['Branch',     version.data.branch === 'unknown' ? '—' : version.data.branch],
                  ['Commit',     version.data.commit_short === 'unknown' ? '—' : version.data.commit_short],
                  ['Build',      version.data.build_time === 'unknown' ? '—' : formatDate(version.data.build_time)],
                  ['Uptime',     formatDuration(version.data.uptime_seconds)],
                  ['Başlangıç',  formatDate(version.data.started_at)],
                ] as [string, string][]).map(([k, v]) => (
                  <div key={k} className="contents">
                    <dt className="text-gray-500">{k}</dt>
                    <dd className="text-gray-300 font-mono truncate">{v}</dd>
                  </div>
                ))}
              </dl>
            ) : <Loading />}
          </Card>

          {/* Konteynerler */}
          <Card wide>
            <div className="flex items-center justify-between gap-3 mb-3">
              <Label>Konteynerler {services.data ? `(${services.data.count})` : ''}</Label>
              {services.data?.timestamp && (
                <span className="text-[11px] text-gray-500">Ölçüm: {formatDate(services.data.timestamp)}</span>
              )}
            </div>
            {services.err ? <ErrText msg={services.err} /> : services.data ? (
              services.data.services.length === 0 ? (
                <div className="text-xs text-yellow-300 bg-yellow-500/10 border border-yellow-600/30 rounded-lg px-3 py-2">
                  Servis snapshot boş. API sunucusunda <span className="font-mono">bash tools/services-status.sh</span> çalıştırıp
                  <span className="font-mono"> /backup/platform-files/.services-status.json</span> dosyasını kontrol et.
                </div>
              ) : (
              <>
              {services.data.status && services.data.status !== 'success' && (
                <div className="mb-3 text-xs text-yellow-300 bg-yellow-500/10 border border-yellow-600/30 rounded-lg px-3 py-2">
                  Son servis ölçümü başarısız; son bilinen container listesi gösteriliyor.
                </div>
              )}
              <div className="overflow-x-auto">
                <table className="w-full text-xs">
                  <thead>
                    <tr className="text-gray-500 border-b border-gray-700">
                      <th className="text-left pb-2 font-medium pr-4">Container</th>
                      <th className="text-left pb-2 font-medium pr-4 hidden sm:table-cell">Image</th>
                      <th className="text-left pb-2 font-medium pr-4">State</th>
                      <th className="text-right pb-2 font-medium pr-4">CPU</th>
                      <th className="text-right pb-2 font-medium pr-4">RAM</th>
                      <th className="text-right pb-2 font-medium pr-4">Restart</th>
                      <th className="text-right pb-2 font-medium">Uptime</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-700/40">
                    {services.data.services.map(s => (
                      <tr key={s.name}>
                        <td className="py-2 pr-4 text-gray-200 font-medium">
                          <div>{s.service || s.name}</div>
                          <div className="text-[11px] text-gray-600 font-normal">{s.name}</div>
                        </td>
                        <td className="py-2 pr-4 text-gray-500 font-mono hidden sm:table-cell">
                          {s.image.split(':')[0]?.split('/').pop() ?? s.image}
                        </td>
                        <td className="py-2 pr-4">
                          <div className="flex items-center gap-2">
                            <Dot state={s.state} />
                            <span className="text-gray-300">{s.state}</span>
                          </div>
                        </td>
                        <td className="py-2 pr-4 text-right text-gray-500">{s.cpu || '—'}</td>
                        <td className="py-2 pr-4 text-right text-gray-500">{s.memory || '—'}</td>
                        <td className="py-2 pr-4 text-right text-gray-500">{s.restart_count ?? '—'}</td>
                        <td className="py-2 text-right text-gray-500">
                          {formatDuration(serviceAgeSeconds(s.started_at, s.age_seconds))}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              </>
              )
            ) : <Loading />}
          </Card>

          {/* Yedekler */}
          <Card wide>
            <div className="flex items-center justify-between mb-3">
              <Label>Yedekler {backups.data ? `(${backups.data.count})` : ''}</Label>
              {backups.data?.last_backup && (
                <div className="flex items-center gap-2 text-xs text-gray-500">
                  <Dot state={backups.data.last_status === 'success' ? 'healthy' : 'unhealthy'} />
                  Son yedek: {formatDate(backups.data.last_backup)}
                </div>
              )}
            </div>
            {backups.err ? <ErrText msg={backups.err} /> : backups.data ? (
              backups.data.count === 0 ? (
                <p className="text-xs text-gray-500">Henüz yedek yok</p>
              ) : (
                <div className="space-y-4">
                  <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                    {[
                      ['Retention', `${backups.data.retention_limit} gün`],
                      ['Toplam', `${backups.data.total_size_mb} MB`],
                      ['Limit', `${backups.data.retention_limit}`],
                      ['Doluluk', `${backups.data.retention_used_pct}%`],
                    ].map(([k, v]) => (
                      <div key={k} className="bg-gray-900/60 border border-gray-700 rounded-lg px-3 py-2">
                        <p className="text-[11px] text-gray-500">{k}</p>
                        <p className="text-sm text-gray-200 font-semibold">{v}</p>
                      </div>
                    ))}
                  </div>
                  <div className="overflow-x-auto">
                    <table className="w-full text-xs">
                      <thead>
                        <tr className="text-gray-500 border-b border-gray-700">
                          <th className="text-left pb-2 font-medium pr-4">Tarih</th>
                          <th className="text-right pb-2 font-medium pr-4">Dosyalar</th>
                          <th className="text-right pb-2 font-medium pr-4">Veritabanı</th>
                          <th className="text-right pb-2 font-medium">Toplam</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-700/40">
                        {backups.data.backups.slice(0, 7).map(b => (
                          <tr key={b.date}>
                            <td className="py-1.5 pr-4 text-gray-300">{formatBackupDate(b.date)}</td>
                            <td className="py-1.5 pr-4 text-right text-gray-400">
                              {b.files_size_mb != null ? `${b.files_size_mb} MB` : '—'}
                            </td>
                            <td className="py-1.5 pr-4 text-right text-gray-400">
                              {b.db_size_mb != null ? `${b.db_size_mb} MB` : '—'}
                            </td>
                            <td className="py-1.5 text-right text-gray-400">{b.total_size_mb} MB</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )
            ) : <Loading />}
          </Card>

        </div>
      </div>
    </div>
  )
}
