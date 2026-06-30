import type { AuthState, AuthTokens, AuthUser } from './types'

const STORAGE_KEY = 'auth'
const LEGACY_STORAGE_KEY = 'auth'

function decodeJwt(token: string): AuthUser {
  const payload = token.split('.')[1]
  const padded = payload + '='.repeat((4 - (payload.length % 4)) % 4)
  const decoded = JSON.parse(atob(padded.replace(/-/g, '+').replace(/_/g, '/')))
  const username = decoded.preferred_username ?? decoded.sub ?? ''
  return {
    sub:                decoded.sub ?? '',
    preferred_username: username,
    personnel_id:       decoded.personnel_id ?? (username ? username.toUpperCase() : undefined),
    roles:              (decoded.roles as string[]) ?? [],
    exp:                decoded.exp ?? 0,
  }
}

function decodeJwtExp(token: string): number | null {
  try {
    const payload = token.split('.')[1]
    const padded = payload + '='.repeat((4 - (payload.length % 4)) % 4)
    const decoded = JSON.parse(atob(padded.replace(/-/g, '+').replace(/_/g, '/')))
    return typeof decoded.exp === 'number' ? decoded.exp : null
  } catch {
    return null
  }
}

export function saveAuth(tokens: AuthTokens): AuthState {
  const user = decodeJwt(tokens.accessToken)
  const refreshJwtExp = decodeJwtExp(tokens.refreshToken)
  const refreshExpiresAt = refreshJwtExp ?? Math.floor(Date.now() / 1000) + (tokens.refreshExpiresIn ?? 0)
  const state: AuthState = {
    token: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    refreshExpiresAt,
    user,
  }
  localStorage.setItem(STORAGE_KEY, JSON.stringify(state))
  sessionStorage.removeItem(LEGACY_STORAGE_KEY)
  return state
}

export function loadAuth(): AuthState | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY) ?? sessionStorage.getItem(LEGACY_STORAGE_KEY)
    if (!raw) return null
    const state = JSON.parse(raw) as AuthState
    if (!state.refreshToken || Date.now() / 1000 > state.refreshExpiresAt) {
      clearAuth()
      return null
    }
    return state
  } catch {
    clearAuth()
    return null
  }
}

export function clearAuth() {
  localStorage.removeItem(STORAGE_KEY)
  sessionStorage.removeItem(LEGACY_STORAGE_KEY)
}

export function isAccessTokenFresh(auth: AuthState, skewSeconds = 30): boolean {
  return Date.now() / 1000 < auth.user.exp - skewSeconds
}

export function canWrite(auth: AuthState, personnelId: string): boolean {
  const roles = auth.user.roles
  if (roles.includes('personnel.files.write.all')) return true
  if (roles.includes('personnel.files.write.self') && auth.user.personnel_id === personnelId) return true
  return false
}
