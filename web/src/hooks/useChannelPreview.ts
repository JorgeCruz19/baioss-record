import { useCallback, useEffect, useRef, useState } from 'react'
import { api, wsPreviewUrl } from '../api/client'

/** Ancho de la imagen (px). 320 ≈ 5–20 KB por cuadro. */
export const PREVIEW_WIDTH = 320
/** Ritmos que ofrece el panel (imágenes por segundo). 1 = mínimo consumo; 10 = movimiento fluido. */
export const PREVIEW_RATES = [1, 5, 10] as const
export type PreviewRate = (typeof PREVIEW_RATES)[number]
export const DEFAULT_PREVIEW_RATE: PreviewRate = 5

const RECONNECT_MS = 2000
/** Aperturas de WebSocket fallidas seguidas antes de pasar al sondeo HTTP (un proxy que no deja pasar WebSocket). */
const WS_FAILURES_BEFORE_POLLING = 3
/** Por HTTP no tiene sentido ir rápido: cada imagen es una petición completa. */
const POLL_MIN_MS = 500
const POLL_RETRY_MS = 5000

export type PreviewState = 'loading' | 'live' | 'unavailable'
export interface PreviewStats { fps: number; kbps: number; transport: 'ws' | 'http' }

/**
 * Vista previa de baja resolución de un canal. La aplicación EMPUJA imágenes JPEG por WebSocket (`/ws/preview/{id}`) al
 * ritmo pedido; aquí solo se pintan. Pensado para gastar poco:
 *  - el servidor marca el ritmo y nunca tiene más de un cuadro en vuelo: con mala red llegan menos cuadros, sin retraso;
 *  - si llega un cuadro mientras se decodifica otro, solo se conserva el ÚLTIMO (los intermedios se descartan);
 *  - se pinta en un `<canvas>` por referencia: un cuadro nuevo NO re-renderiza la tarjeta (solo el resumen, 1 vez/s);
 *  - con la pestaña oculta o la tarjeta fuera de pantalla se CIERRA el WebSocket, y la aplicación deja de capturar;
 *  - si el WebSocket no llega a abrir varias veces, se recurre al sondeo HTTP de `preview.jpg`.
 * `containerRef` va en el contenedor (lo que se observa para saber si está a la vista) y `canvasRef` en el `<canvas>`.
 */
export function useChannelPreview(channelId: string, enabled: boolean, fps: number) {
  const [state, setState] = useState<PreviewState>('loading')
  const [hasFrame, setHasFrame] = useState(false)
  const [stats, setStats] = useState<PreviewStats | null>(null)
  const canvas = useRef<HTMLCanvasElement | null>(null)
  const inView = useRef(true)
  const observer = useRef<IntersectionObserver | null>(null)
  /** Re-evalúa si toca estar conectado (lo llaman la visibilidad de la pestaña y el IntersectionObserver). */
  const sync = useRef<() => void>(() => {})

  const containerRef = useCallback((node: HTMLElement | null) => {
    observer.current?.disconnect()
    observer.current = null
    if (!node || typeof IntersectionObserver === 'undefined') { inView.current = true; return }
    observer.current = new IntersectionObserver(
      entries => { inView.current = entries.some(e => e.isIntersecting); sync.current() },
      { rootMargin: '120px' },
    )
    observer.current.observe(node)
  }, [])
  const canvasRef = useCallback((node: HTMLCanvasElement | null) => { canvas.current = node }, [])

  useEffect(() => {
    if (!enabled) { setState('loading'); setHasFrame(false); setStats(null); return }

    let disposed = false
    let socket: WebSocket | null = null
    let reconnectTimer: number | undefined
    let pollTimer: number | undefined
    let polling = false
    let wsFailures = 0
    let live = false
    // Decodificación: solo el último cuadro pendiente sobrevive.
    let pending: Blob | null = null
    let drawing = false
    // Ventana de medida (se vacía cada segundo).
    let frames = 0
    let bytes = 0
    let windowStart = performance.now()
    const aborter = new AbortController()

    const draw = async (blob: Blob) => {
      pending = blob
      if (drawing) return
      drawing = true
      try {
        while (pending && !disposed) {
          const next = pending
          pending = null
          const bitmap = await createImageBitmap(next)
          const c = canvas.current
          if (c && !disposed) {
            if (c.width !== bitmap.width || c.height !== bitmap.height) { c.width = bitmap.width; c.height = bitmap.height }
            c.getContext('2d')?.drawImage(bitmap, 0, 0)
            frames++
            if (!live) { live = true; setState('live'); setHasFrame(true) }
          }
          bitmap.close()
        }
      } catch { /* un cuadro que no decodifica se ignora: el siguiente llega enseguida */ }
      finally { drawing = false }
    }

    // La aplicación dice que ahora no hay imagen (entrada reasignándose, canal simulado): el último cuadro se queda en
    // pantalla con su aviso, y el primero que vuelva a llegar lo devuelve a «en vivo».
    const markUnavailable = () => { if (!disposed) { live = false; setState('unavailable') } }

    const closeSocket = () => {
      const s = socket
      socket = null
      if (s) { s.onopen = s.onmessage = s.onclose = s.onerror = null; try { s.close() } catch { /* ya cerrado */ } }
    }

    const connect = () => {
      const s = new WebSocket(wsPreviewUrl(channelId, PREVIEW_WIDTH, fps))
      s.binaryType = 'blob'
      socket = s
      let opened = false
      s.onopen = () => { opened = true; wsFailures = 0 }
      s.onmessage = e => {
        if (typeof e.data === 'string') { if (e.data === 'unavailable') markUnavailable(); return }
        const blob = e.data as Blob
        bytes += blob.size
        void draw(blob)
      }
      s.onclose = () => {
        if (socket !== s || disposed) return
        socket = null
        if (!opened && ++wsFailures >= WS_FAILURES_BEFORE_POLLING) polling = true
        reconnectTimer = window.setTimeout(() => sync.current(), RECONNECT_MS)
      }
    }

    const poll = async () => {
      pollTimer = undefined
      if (disposed || !polling) return
      let delay = Math.max(POLL_MIN_MS, 1000 / fps)
      try {
        const res = await api.get<Blob>(`/channels/${channelId}/preview.jpg`, {
          params: { w: PREVIEW_WIDTH }, responseType: 'blob', signal: aborter.signal, timeout: 5000,
        })
        if (disposed) return
        bytes += res.data.size
        void draw(res.data)
      } catch {
        if (disposed) return
        markUnavailable()
        delay = POLL_RETRY_MS
      }
      if (shouldRun()) pollTimer = window.setTimeout(poll, delay)
    }

    const shouldRun = () => document.visibilityState === 'visible' && inView.current

    sync.current = () => {
      if (disposed) return
      window.clearTimeout(reconnectTimer)
      if (!shouldRun()) { closeSocket(); window.clearTimeout(pollTimer); pollTimer = undefined; return }
      if (polling) { if (pollTimer === undefined) void poll() }
      else if (!socket) connect()
    }

    const onVisibility = () => sync.current()
    document.addEventListener('visibilitychange', onVisibility)

    const statsTimer = window.setInterval(() => {
      const now = performance.now()
      const seconds = (now - windowStart) / 1000
      if (seconds <= 0) return
      const running = shouldRun() && (socket !== null || polling)
      setStats(running ? { fps: frames / seconds, kbps: (bytes * 8) / 1000 / seconds, transport: polling ? 'http' : 'ws' } : null)
      frames = 0; bytes = 0; windowStart = now
    }, 1000)

    sync.current()

    return () => {
      disposed = true
      sync.current = () => {}
      document.removeEventListener('visibilitychange', onVisibility)
      window.clearInterval(statsTimer)
      window.clearTimeout(reconnectTimer)
      window.clearTimeout(pollTimer)
      aborter.abort()
      closeSocket()
    }
  }, [channelId, enabled, fps])

  useEffect(() => () => observer.current?.disconnect(), [])

  return { containerRef, canvasRef, state, hasFrame, stats }
}
