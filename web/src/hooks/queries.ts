import { useRef } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  deleteTask, getActiveTasks, getChannels, getEvents, getLicense, getRecordings, getSchedule, getStorageSettings, getStorageStatus,
  putStorageSettings, saveTask, setProtection, setTaskEnabled, skipScheduled, startRecording, stopRecording,
} from '../api/record'
import type { EventsFilter, RecordingsFilter } from '../api/record'
import type { ProtectionLevel, StorageSettings, TaskDraft } from '../api/types'

/**
 * Primera carga de una lista: cuándo enseñar el esqueleto y cuándo el aviso de «sin conexión». React Query 5 devuelve
 * una consulta SIN DATOS a «pendiente» (y borra su error) cada vez que reintenta; con el sondeo periódico, la pantalla
 * alternaba cada segundo entre el aviso y los esqueletos, y los botones del aviso aparecían y desaparecían. Aquí el
 * último error se CONSERVA mientras no lleguen datos: el esqueleto solo sale antes del primer fallo.
 */
export function useFirstLoad(query: { data: unknown; error: unknown; isPending: boolean }): { loading: boolean; error: unknown } {
  const lastError = useRef<unknown>(null)
  if (query.data !== undefined) lastError.current = null
  else if (query.error) lastError.current = query.error
  return { loading: query.isPending && lastError.current === null, error: query.error ?? lastError.current }
}

/** Claves de React Query. Las listas cuelgan de un prefijo para poder invalidarlas en bloque (p. ej. desde el WebSocket). */
export const qk = {
  channels: ['channels'] as const,
  recordings: (f: RecordingsFilter) => ['recordings', f] as const,
  events: (f: EventsFilter) => ['events', f] as const,
  license: ['license'] as const,
  storageStatus: ['storage', 'status'] as const,
  storageSettings: ['storage', 'settings'] as const,
  schedule: (channel: string) => ['schedule', 'list', channel] as const,
  activeTasks: ['schedule', 'active'] as const,
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
    mutationFn: (v: { id: string; name?: string | null; operator?: string | null }) => stopRecording(v.id, v.name, v.operator),
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

// ---------- Programación (tareas automáticas) ----------

/** La lista completa: cambia poco (y el WebSocket avisa de cada alta/baja/arranque), así que se sondea despacio. */
export const useSchedule = (channel = '') =>
  useQuery({ queryKey: qk.schedule(channel), queryFn: () => getSchedule(channel), placeholderData: keepPreviousData, refetchInterval: 15_000 })

/** Solo lo que está EN MARCHA, para la tarjeta de cada canal: ligero, y trae lo que le queda con el reloj del Record. */
export const useActiveTasks = () => useQuery({ queryKey: qk.activeTasks, queryFn: getActiveTasks, refetchInterval: 5_000 })

function useScheduleMutation<V>(fn: (v: V) => Promise<unknown>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: ['schedule'] })
      void qc.invalidateQueries({ queryKey: ['events'] })
    },
  })
}

export const useSaveTask = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (v: { draft: TaskDraft; id?: string }) => saveTask(v.draft, v.id),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: ['schedule'] })
      void qc.invalidateQueries({ queryKey: ['events'] })
    },
  })
}
export const useSetTaskEnabled = () => useScheduleMutation((v: { id: string; enabled: boolean; operator: string | null }) => setTaskEnabled(v.id, v.enabled, v.operator))
export const useDeleteTask = () => useScheduleMutation((v: { id: string; operator: string | null }) => deleteTask(v.id, v.operator))

/** Saltar la grabación programada en curso de un canal (el ⏏ de la aplicación). */
export function useSkipScheduled() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (channelId: string) => skipScheduled(channelId),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.channels })
      void qc.invalidateQueries({ queryKey: ['schedule'] })
      void qc.invalidateQueries({ queryKey: ['recordings'] })
      void qc.invalidateQueries({ queryKey: ['events'] })
    },
  })
}

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
