import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  getChannels, getEvents, getLicense, getRecordings, getStorageSettings, getStorageStatus, putStorageSettings,
  setProtection, startRecording, stopRecording,
} from '../api/record'
import type { EventsFilter, RecordingsFilter } from '../api/record'
import type { ProtectionLevel, StorageSettings } from '../api/types'

/** Claves de React Query. Las listas cuelgan de un prefijo para poder invalidarlas en bloque (p. ej. desde el WebSocket). */
export const qk = {
  channels: ['channels'] as const,
  recordings: (f: RecordingsFilter) => ['recordings', f] as const,
  events: (f: EventsFilter) => ['events', f] as const,
  license: ['license'] as const,
  storageStatus: ['storage', 'status'] as const,
  storageSettings: ['storage', 'settings'] as const,
}

/** Estado de los canales: sondeo de 1 s (la API es local y barata); se pausa con la pestaña en segundo plano. */
export const useChannels = () =>
  useQuery({ queryKey: qk.channels, queryFn: getChannels, refetchInterval: 1000 })

export function useStartRecording() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (v: { id: string; operator: string | null }) => startRecording(v.id, v.operator),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.channels })
      void qc.invalidateQueries({ queryKey: ['recordings'] })
    },
  })
}

export function useStopRecording() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => stopRecording(id),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.channels })
      void qc.invalidateQueries({ queryKey: ['recordings'] })
      void qc.invalidateQueries({ queryKey: ['events'] })
    },
  })
}

export const useRecordings = (f: RecordingsFilter) =>
  useQuery({ queryKey: qk.recordings(f), queryFn: () => getRecordings(f), placeholderData: keepPreviousData, refetchInterval: 10_000 })

export function useSetProtection() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (v: { id: string; level: ProtectionLevel }) => setProtection(v.id, v.level),
    onSettled: () => { void qc.invalidateQueries({ queryKey: ['recordings'] }) },
  })
}

export const useEvents = (f: EventsFilter) =>
  useQuery({ queryKey: qk.events(f), queryFn: () => getEvents(f), placeholderData: keepPreviousData, refetchInterval: 10_000 })

export const useLicense = () => useQuery({ queryKey: qk.license, queryFn: getLicense, refetchInterval: 60_000 })
export const useStorageStatus = () => useQuery({ queryKey: qk.storageStatus, queryFn: getStorageStatus, refetchInterval: 5_000 })
export const useStorageSettings = () => useQuery({ queryKey: qk.storageSettings, queryFn: getStorageSettings })

export function useSaveStorageSettings() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (s: StorageSettings) => putStorageSettings(s),
    onSuccess: saved => {
      qc.setQueryData(qk.storageSettings, saved) // la versión SANEADA que aplicó la aplicación
      void qc.invalidateQueries({ queryKey: qk.storageStatus })
    },
  })
}
