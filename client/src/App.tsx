import { useState, useEffect } from 'react'
import { clearAuth, isAccessTokenFresh, loadAuth, saveAuth } from './auth'
import { refreshLogin } from './api'
import type { AuthState } from './types'
import LoginPage from './components/LoginPage'
import Dashboard from './components/Dashboard'

export default function App() {
  const [auth, setAuth] = useState<AuthState | null>(null)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    let cancelled = false

    async function restoreSession() {
      const stored = loadAuth()
      if (!stored) {
        if (!cancelled) setReady(true)
        return
      }

      if (isAccessTokenFresh(stored)) {
        if (!cancelled) {
          setAuth(stored)
          setReady(true)
        }
        return
      }

      try {
        const refreshed = saveAuth(await refreshLogin(stored.refreshToken))
        if (!cancelled) setAuth(refreshed)
      } catch {
        clearAuth()
        if (!cancelled) setAuth(null)
      } finally {
        if (!cancelled) setReady(true)
      }
    }

    restoreSession()
    return () => { cancelled = true }
  }, [])

  useEffect(() => {
    if (!auth) return

    const now = Date.now() / 1000
    const refreshInMs = Math.max((auth.user.exp - now - 60) * 1000, 0)

    const timeoutId = window.setTimeout(async () => {
      try {
        const refreshed = saveAuth(await refreshLogin(auth.refreshToken))
        setAuth(refreshed)
      } catch {
        clearAuth()
        setAuth(null)
      }
    }, refreshInMs)

    return () => window.clearTimeout(timeoutId)
  }, [auth])

  if (!ready) return null

  if (!auth) {
    return (
      <LoginPage
        onLogin={(tokens) => setAuth(saveAuth(tokens))}
      />
    )
  }

  return (
    <Dashboard
      auth={auth}
      onLogout={() => { clearAuth(); setAuth(null) }}
    />
  )
}
