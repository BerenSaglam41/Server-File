import type { AuthState, Personnel, PersonnelFile } from './types'

async function apiFetch(url: string, options: RequestInit = {}) {
  return fetch(url, { ...options, credentials: 'include' })
}

// ─── BFF AUTH ────────────────────────────────────────────────────────────────

export async function bffLogin(username: string, password: string): Promise<AuthState> {
  const res = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ username, password }),
  })
  if (!res.ok) throw new Error('Kullanıcı adı veya şifre hatalı')
  return parseBffResponse(await res.json())
}

export async function bffRefresh(): Promise<AuthState> {
  const res = await fetch('/api/auth/refresh', {
    method: 'POST',
    credentials: 'include',
  })
  if (!res.ok) throw new Error('Oturum süresi doldu')
  return parseBffResponse(await res.json())
}

export async function bffLogout(): Promise<void> {
  await fetch('/api/auth/logout', { method: 'POST', credentials: 'include' })
}

function parseBffResponse(data: { user: AuthState['user']; expiresAt: number }): AuthState {
  return { user: data.user, expiresAt: data.expiresAt }
}

// ─── PERSONNEL ───────────────────────────────────────────────────────────────

export async function searchPersonnel(q: string): Promise<Personnel[]> {
  const res = await apiFetch(`/api/personnel?search=${encodeURIComponent(q)}`)
  if (!res.ok) throw new Error('Arama başarısız')
  return res.json()
}

export async function getPersonnelFiles(personnelId: string): Promise<PersonnelFile[]> {
  const res = await apiFetch(`/api/personnel/${encodeURIComponent(personnelId)}/files`)
  if (res.status === 403) throw new Error('Bu personele erişim yetkiniz yok')
  if (!res.ok) throw new Error('Dosyalar alınamadı')
  return res.json()
}

function toUrlSegment(relationType: string) {
  return relationType.replace(/_/g, '-')
}

function xhrUpload(
  url: string,
  file: File,
  onProgress: ((pct: number) => void) | undefined,
  errors: Record<number, string>,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const form = new FormData()
    form.append('file', file)
    const xhr = new XMLHttpRequest()
    xhr.withCredentials = true
    if (onProgress) {
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100))
      }
    }
    xhr.onload = () => {
      const msg = errors[xhr.status]
      if (msg) return reject(new Error(msg))
      if (xhr.status < 200 || xhr.status >= 300) return reject(new Error('Yükleme başarısız'))
      resolve()
    }
    xhr.onerror = () => reject(new Error('Yükleme başarısız'))
    xhr.open('POST', url)
    xhr.send(form)
  })
}

export function uploadFile(
  personnelId: string,
  relationType: string,
  file: File,
  onProgress?: (pct: number) => void,
): Promise<void> {
  return xhrUpload(
    `/api/personnel/${encodeURIComponent(personnelId)}/${toUrlSegment(relationType)}`,
    file,
    onProgress,
    {
      415: 'Desteklenmeyen dosya formatı (PDF, JPG, PNG, WebP)',
      413: 'Dosya çok büyük (maks. 10MB)',
      403: 'Bu işlem için yetkiniz yok',
      409: 'Bu dosya zaten yüklü',
    },
  )
}

export async function archiveFile(personnelId: string, fileId: string): Promise<void> {
  const res = await apiFetch(
    `/api/personnel/${encodeURIComponent(personnelId)}/files/${fileId}/archive`,
    { method: 'POST' }
  )
  if (res.status === 403) throw new Error('Arşivleme için yetkiniz yok')
  if (!res.ok) throw new Error('Arşivleme başarısız')
}

export async function archiveSinglePrimary(personnelId: string, relationType: string): Promise<void> {
  const res = await apiFetch(
    `/api/personnel/${encodeURIComponent(personnelId)}/${toUrlSegment(relationType)}/archive`,
    { method: 'POST' }
  )
  if (res.status === 403) throw new Error('Arşivleme için yetkiniz yok')
  if (!res.ok) throw new Error('Arşivleme başarısız')
}

export async function fetchFileBlob(
  personnelId: string,
  fileId: string,
): Promise<{ blob: Blob; contentType: string; fileName: string }> {
  const res = await apiFetch(
    `/api/personnel/${encodeURIComponent(personnelId)}/files/${fileId}/content`
  )
  if (!res.ok) throw new Error('İndirme başarısız')
  return extractBlob(res)
}

// ─── FLEET (FİLO) ────────────────────────────────────────────────────────────

export async function getVehicleFiles(vehicleId: string): Promise<PersonnelFile[]> {
  const res = await apiFetch(`/api/vehicles/${encodeURIComponent(vehicleId)}/files`)
  if (res.status === 403) throw new Error('Bu araca erişim yetkiniz yok')
  if (!res.ok) throw new Error('Dosyalar alınamadı')
  return res.json()
}

export function uploadVehicleFile(
  vehicleId: string,
  relationType: string,
  file: File,
  onProgress?: (pct: number) => void,
): Promise<void> {
  return xhrUpload(
    `/api/vehicles/${encodeURIComponent(vehicleId)}/${toUrlSegment(relationType)}`,
    file,
    onProgress,
    {
      415: 'Desteklenmeyen dosya formatı (PDF, JPG, PNG, WebP)',
      413: 'Dosya çok büyük (maks. 25MB)',
      403: 'Bu işlem için yetkiniz yok',
      409: 'Bu dosya zaten yüklü',
    },
  )
}

export async function archiveVehiclePrimary(vehicleId: string, relationType: string): Promise<void> {
  const res = await apiFetch(
    `/api/vehicles/${encodeURIComponent(vehicleId)}/${toUrlSegment(relationType)}/archive`,
    { method: 'POST' }
  )
  if (res.status === 403) throw new Error('Arşivleme için yetkiniz yok')
  if (!res.ok) throw new Error('Arşivleme başarısız')
}

export async function fetchVehicleFileContent(
  vehicleId: string,
  relationType: string,
): Promise<{ blob: Blob; contentType: string; fileName: string }> {
  const res = await apiFetch(
    `/api/vehicles/${encodeURIComponent(vehicleId)}/${toUrlSegment(relationType)}/content`
  )
  if (!res.ok) throw new Error('İndirme başarısız')
  return extractBlob(res)
}

// ─── YARDIMCI ────────────────────────────────────────────────────────────────

async function extractBlob(res: Response): Promise<{ blob: Blob; contentType: string; fileName: string }> {
  const blob        = await res.blob()
  const contentType = res.headers.get('content-type') ?? 'application/octet-stream'
  const cd          = res.headers.get('content-disposition') ?? ''
  const rfc5987Match = cd.match(/filename\*=UTF-8''([^;\s]+)/i)
  const legacyMatch  = cd.match(/filename="([^"]+)"/)
  const rawFileName  = rfc5987Match
    ? decodeURIComponent(rfc5987Match[1])
    : legacyMatch ? legacyMatch[1] : null
  const fileName = rawFileName ?? `dosya.${contentType.split('/')[1] ?? 'bin'}`
  return { blob, contentType, fileName }
}
