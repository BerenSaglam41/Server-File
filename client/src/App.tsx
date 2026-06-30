import { useState, useEffect } from 'react'
import { isAccessTokenFresh } from './auth'
import { bffRefresh, bffLogout } from './api'
import type { AuthState } from './types'
import LoginPage from './components/LoginPage'
import Dashboard from './components/Dashboard'

export default function App() {
  const [auth, setAuth] = useState<AuthState | null>(null)
  const [ready, setReady] = useState(false)

  // Sayfa yüklendiğinde rt cookie ile oturumu geri yükle
  useEffect(() => {
    let cancelled = false
    bffRefresh()
      .then(state => { if (!cancelled) setAuth(state) })
      .catch(() => { if (!cancelled) setAuth(null) })
      .finally(() => { if (!cancelled) setReady(true) })
    return () => { cancelled = true }
  }, [])

  // Access token süresine göre proaktif refresh zamanla
  useEffect(() => {
    if (!auth) return
    if (isAccessTokenFresh(auth)) {
      const delayMs = Math.max((auth.expiresAt - Date.now() / 1000 - 60) * 1000, 0)
      const id = window.setTimeout(async () => {
        try { setAuth(await bffRefresh()) }
        catch { setAuth(null) }
      }, delayMs)
      return () => window.clearTimeout(id)
    }
  }, [auth])

  if (!ready) return null

  if (!auth) {
    return <LoginPage onLogin={setAuth} />
  }

  return (
    <Dashboard
      auth={auth}
      onLogout={async () => { await bffLogout(); setAuth(null) }}
    />
  )
}
