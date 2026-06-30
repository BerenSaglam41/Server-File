export interface AuthUser {
  sub: string
  preferred_username: string
  personnel_id?: string
  roles: string[]
  exp: number
}

export interface AuthState {
  token: string
  refreshToken: string
  refreshExpiresAt: number
  user: AuthUser
}

export interface AuthTokens {
  accessToken: string
  refreshToken: string
  refreshExpiresIn?: number
}

export interface Personnel {
  personnelId: string
  displayName: string
  department: string | null
  title: string | null
}

export interface PersonnelFile {
  fileId: string
  domain: string
  relationType: string
  contentType: string
  originalFileName: string | null
  extension: string
  sizeBytes: number
  sha256: string
  classification: string
  status: string
  createdAt: string
  etag: string
}

export const RELATION_TYPE_LABELS: Record<string, string> = {
  cv:                'Özgeçmiş',
  photo:             'Fotoğraf',
  official_document: 'Resmi Evrak',
  document:          'Belge',
  attachment:        'Ek Dosya',
  report:            'Rapor',
}

export const SINGLE_PRIMARY_TYPES = new Set(['cv', 'photo', 'official_document'])

export const UPLOAD_RELATION_TYPES = [
  { value: 'cv',                label: 'Özgeçmiş (CV)',   single: true },
  { value: 'photo',             label: 'Fotoğraf',         single: true },
  { value: 'official_document', label: 'Resmi Evrak',      single: true },
  { value: 'document',          label: 'Belge',            single: false },
  { value: 'attachment',        label: 'Ek Dosya',         single: false },
] as const

export type UploadRelationType = typeof UPLOAD_RELATION_TYPES[number]['value']
