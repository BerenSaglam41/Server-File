export interface AuthUser {
  sub: string
  preferred_username: string
  personnel_id?: string
  vehicle_id?: string
  roles: string[]
}

export interface AuthState {
  user: AuthUser
  expiresAt: number   // Unix timestamp — access token expiry (from BFF response)
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

export const VEHICLE_UPLOAD_RELATION_TYPES = [
  { value: 'photo',             label: 'Fotoğraf',   single: true  },
  { value: 'document',          label: 'Belge',       single: true  },
  { value: 'official_document', label: 'Resmi Evrak', single: true  },
  { value: 'attachment',        label: 'Ek Dosya',    single: false },
  { value: 'report',            label: 'Rapor',       single: false },
] as const

export const VEHICLE_SINGLE_PRIMARY_TYPES = new Set(['photo', 'document', 'official_document'])

export type VehicleUploadRelationType = typeof VEHICLE_UPLOAD_RELATION_TYPES[number]['value']
