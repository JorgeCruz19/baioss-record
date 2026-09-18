import { api } from './client'
import type {
  ChannelStatus, EventEntry, LicenseInfo, ProtectionLevel, RecordingSummary, Severity, StorageSettings, StorageSnapshot,
} from './types'

export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'

export const getChannels = () => api.get<ChannelStatus[]>('/channels').then(r => r.data)
export const getChannelStatus = (id: string) => api.get<ChannelStatus>(`/channels/${id}/status`).then(r => r.data)

/**
 * Inicia la grabación de un canal. La API exige un `profileId` en el cuerpo, pero el canal graba SIEMPRE con el
 * perfil vigente elegido en la aplicación de escritorio (el motor ignora ese id), así que se envía el GUID vacío.
 * El `operator` queda en la auditoría como quien la pidió (origen «API»).
 */
export const startRecording = (id: string, operator: string | null) =>
  api.post<{ sessionId: string }>(`/channels/${id}/recording/start`, { profileId: EMPTY_GUID, operator: operator || null })
    .then(r => r.data)

export const stopRecording = (id: string) => api.post(`/channels/${id}/recording/stop`).then(() => undefined)

export interface RecordingsFilter { channel?: string; days?: number }
export const getRecordings = (f: RecordingsFilter) =>
  api.get<RecordingSummary[]>('/recordings', { params: { channel: f.channel || undefined, days: f.days } }).then(r => r.data)

export const setProtection = (id: string, level: ProtectionLevel) =>
  api.post<{ id: string; protection: ProtectionLevel }>(`/recordings/${id}/protection`, { level }).then(r => r.data)

export interface EventsFilter { days?: number; channel?: string; category?: string; severity?: Severity | ''; take?: number }
export const getEvents = (f: EventsFilter) =>
  api.get<EventEntry[]>('/events', {
    params: {
      days: f.days,
      channel: f.channel || undefined,
      category: f.category || undefined,
      severity: f.severity || undefined,
      take: f.take,
    },
  }).then(r => r.data)

export const getLicense = () => api.get<LicenseInfo>('/license').then(r => r.data)
export const getStorageStatus = () => api.get<StorageSnapshot>('/storage/status').then(r => r.data)
export const getStorageSettings = () => api.get<StorageSettings>('/storage/settings').then(r => r.data)
/** Devuelve los ajustes SANEADOS que la aplicación aplicó realmente (puede acotar valores). */
export const putStorageSettings = (s: StorageSettings) => api.put<StorageSettings>('/storage/settings', s).then(r => r.data)
