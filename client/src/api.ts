import type { AuthTokens, Personnel, PersonnelFile } from './types'

async function apiFetch(url: string, token: string, options: RequestInit = {}) {
  const res = await fetch(url, {
    ...options,
    headers: {
      ...options.headers,
      Authorization: `Bearer ${token}`,
    },
  })
  return res
}

export async function login(username: string, password: string): Promise<AuthTokens> {
  const res = await fetch('/realms/platform/protocol/openid-connect/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'password',
      client_id:  'frontend-test',
      username,
      password,
    }),
  })
  if (!res.ok) throw new Error('Kullanıcı adı veya şifre hatalı')
  const data = await res.json()
  return {
    accessToken: data.access_token as string,
    refreshToken: data.refresh_token as string,
    refreshExpiresIn: data.refresh_expires_in as number | undefined,
  }
}

export async function refreshLogin(refreshToken: string): Promise<AuthTokens> {
  const res = await fetch('/realms/platform/protocol/openid-connect/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'refresh_token',
      client_id:  'frontend-test',
      refresh_token: refreshToken,
    }),
  })
  if (!res.ok) throw new Error('Oturum süresi doldu')
  const data = await res.json()
  return {
    accessToken: data.access_token as string,
    refreshToken: (data.refresh_token as string | undefined) ?? refreshToken,
    refreshExpiresIn: data.refresh_expires_in as number | undefined,
  }
}

export async function searchPersonnel(q: string, token: string): Promise<Personnel[]> {
  const res = await apiFetch(`/api/personnel?search=${encodeURIComponent(q)}`, token)
  if (!res.ok) throw new Error('Arama başarısız')
  return res.json()
}

export async function getPersonnelFiles(personnelId: string, token: string): Promise<PersonnelFile[]> {
  const res = await apiFetch(`/api/personnel/${encodeURIComponent(personnelId)}/files`, token)
  if (res.status === 403) throw new Error('Bu personele erişim yetkiniz yok')
  if (!res.ok) throw new Error('Dosyalar alınamadı')
  return res.json()
}

function toUrlSegment(relationType: string) {
  return relationType.replace(/_/g, '-')
}

export async function uploadFile(
  personnelId: string,
  relationType: string,
  file: File,
  token: string
): Promise<void> {
  const form = new FormData()
  form.append('file', file)
  const res = await apiFetch(`/api/personnel/${encodeURIComponent(personnelId)}/${toUrlSegment(relationType)}`, token, {
    method: 'POST',
    body: form,
  })
  if (res.status === 415) throw new Error('Desteklenmeyen dosya formatı (PDF, JPG, PNG, WebP)')
  if (res.status === 413) throw new Error('Dosya çok büyük (maks. 10MB)')
  if (res.status === 403) throw new Error('Bu işlem için yetkiniz yok')
  if (!res.ok) throw new Error('Yükleme başarısız')
}

export async function archiveFile(
  personnelId: string,
  fileId: string,
  token: string
): Promise<void> {
  const res = await apiFetch(
    `/api/personnel/${encodeURIComponent(personnelId)}/files/${fileId}/archive`,
    token,
    { method: 'POST' }
  )
  if (res.status === 403) throw new Error('Arşivleme için yetkiniz yok')
  if (!res.ok) throw new Error('Arşivleme başarısız')
}

export async function archiveSinglePrimary(
  personnelId: string,
  relationType: string,
  token: string
): Promise<void> {
  const res = await apiFetch(
    `/api/personnel/${encodeURIComponent(personnelId)}/${toUrlSegment(relationType)}/archive`,
    token,
    { method: 'POST' }
  )
  if (res.status === 403) throw new Error('Arşivleme için yetkiniz yok')
  if (!res.ok) throw new Error('Arşivleme başarısız')
}

export async function fetchFileBlob(
  personnelId: string,
  fileId: string,
  token: string
): Promise<{ blob: Blob; contentType: string; fileName: string }> {
  const res = await apiFetch(
    `/api/personnel/${encodeURIComponent(personnelId)}/files/${fileId}/content`,
    token
  )
  if (!res.ok) throw new Error('İndirme başarısız')
  const blob = await res.blob()
  const contentType = res.headers.get('content-type') ?? 'application/octet-stream'
  const cd = res.headers.get('content-disposition') ?? ''
  const nameMatch = cd.match(/filename="?([^";]+)"?/)
  const fileName = nameMatch ? nameMatch[1] : `dosya.${contentType.split('/')[1] ?? 'bin'}`
  return { blob, contentType, fileName }
}
