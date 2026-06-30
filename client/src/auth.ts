import type { AuthState } from './types'

export function isAccessTokenFresh(auth: AuthState, skewSeconds = 30): boolean {
  return Date.now() / 1000 < auth.expiresAt - skewSeconds
}

export function canWrite(auth: AuthState, personnelId: string): boolean {
  const roles = auth.user.roles
  if (roles.includes('personnel.files.write.all')) return true
  if (roles.includes('personnel.files.write.self') && auth.user.personnel_id === personnelId) return true
  return false
}

export function canVehicleWrite(auth: AuthState, vehicleId: string): boolean {
  return !!auth.user.vehicle_id && auth.user.vehicle_id === vehicleId
}
