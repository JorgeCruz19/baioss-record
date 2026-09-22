import { api } from './client'
import type {
  ActiveTask, ChannelStatus, EventEntry, LicenseInfo, ProtectionLevel, RecordingSummary, ScheduleList, ScheduledTask, Severity,
  StopResult, StorageSettings, StorageSnapshot, TaskDraft,
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

/**
 * Detiene la grabación. Con `name`, la aplicación guarda el archivo con ese nombre (como su diálogo al detener una
 * grabación manual; si ya existe añade « 1», « 2»…) y contesta qué pasó. Sin nombre contesta 204: devuelve null y el
 * archivo queda con el temporal `{canal}_{fecha_hora}`.
 */
export const stopRecording = (id: string, name?: string | null, operator?: string | null) => {
  const clean = name?.trim()
  return api
    .post<StopResult | ''>(`/channels/${id}/recording/stop`, clean ? { name: clean, operator: operator?.trim() || null } : undefined)
    .then(r => (r.status === 204 || !r.data ? null : r.data))
}

// ---------- Programación (tareas automáticas) ----------
export const getSchedule = (channel?: string) =>
  api.get<ScheduleList>('/schedule', { params: { channel: channel || undefined } }).then(r => r.data)
export const getActiveTasks = () => api.get<ActiveTask[]>('/schedule/active').then(r => r.data)
/** Crear o editar (con `id`). Devuelve la tarea guardada y los avisos que no impiden guardar. */
export const saveTask = (draft: TaskDraft, id?: string) =>
  (id ? api.put<{ job: ScheduledTask; notes: string[] }>(`/schedule/${id}`, draft) : api.post<{ job: ScheduledTask; notes: string[] }>('/schedule', draft))
    .then(r => r.data)
export const setTaskEnabled = (id: string, enabled: boolean, operator: string | null) =>
  api.post<ScheduledTask>(`/schedule/${id}/enabled`, { enabled, operator }).then(r => r.data)
export const deleteTask = (id: string, operator: string | null) =>
  api.delete(`/schedule/${id}`, { params: { operator: operator || undefined } }).then(() => undefined)
/** Salta la grabación programada EN CURSO de un canal: la detiene ya y solo esa ocurrencia; las siguientes siguen. */
export const skipScheduled = (channelId: string) => api.post(`/channels/${channelId}/schedule/skip`).then(() => undefined)

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
