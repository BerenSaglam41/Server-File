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

// ─── OPS CONSOLE ─────────────────────────────────────────────────────────────

export interface OpsHealth {
  status: string
  timestamp: string
  services: { name: string; status: string; latency_ms: number | null; reason?: string | null }[]
}

export interface OpsServices {
  count: number
  status?: string
  timestamp?: string | null
  services: {
    name: string
    service: string
    image: string
    status: string
    state: string
    created: string
    started_at?: string | null
    age_seconds?: number | null
    restart_count?: number | null
    cpu?: string | null
    memory?: string | null
  }[]
}

export interface OpsDisk {
  status: string
  timestamp: string | null
  api_server_pct: number | null
  files01_pct: number | null
  reason: string | null
}

export interface OpsAlerts {
  count: number
  alerts: { source: string; severity: string; status: string; reason: string; timestamp: string }[]
}

export interface OpsBackup {
  date: string
  files_size_mb: number | null
  db_size_mb: number | null
  total_size_mb: number
  created: string
}

export interface OpsBackups {
  count: number
  total_size_mb: number
  retention_limit: number
  retention_used_pct: number
  last_backup: string | null
  last_status: string | null
  backups: OpsBackup[]
}

export interface OpsVersion {
  service: string
  version: string
  environment: string
  commit_hash: string
  commit_short: string
  branch: string
  build_time: string
  started_at: string
  uptime_seconds: number
  timestamp: string
}

export interface OpsDashboard {
  timestamp: string
  health: OpsHealth
  services: OpsServices
  disk: OpsDisk
  alerts: OpsAlerts
  backups: OpsBackups
  version: OpsVersion
}
