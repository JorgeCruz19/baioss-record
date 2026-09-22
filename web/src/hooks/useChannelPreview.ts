import { useCallback, useEffect, useRef, useState } from 'react'
import { api, wsPreviewUrl } from '../api/client'
import { useConnection } from '../api/connection'

/** Ancho de la imagen (px). 320 ≈ 5–20 KB por cuadro. */
export const PREVIEW_WIDTH = 320
/** Ritmos que ofrece el panel (imágenes por segundo). 1 = mínimo consumo; 10 = movimiento fluido. */
export const PREVIEW_RATES = [1, 5, 10] as const
export type PreviewRate = (typeof PREVIEW_RATES)[number]
export const DEFAULT_PREVIEW_RATE: PreviewRate = 5

const RECONNECT_MS = 2000
/** Lo que tiene que DURAR a la vista una tarjeta para conectar: pasar por ella haciendo scroll (o un parpadeo de
 *  visibilidad de la pestaña) no abre un WebSocket para cerrarlo al instante. */
const CONNECT_DELAY_MS = 250
/** Aperturas de WebSocket fallidas seguidas antes de COMPROBAR por HTTP si el Record responde (ver `probe`). */
const WS_FAILURES_BEFORE_PROBE = 3
/** Desde el sondeo HTTP se vuelve a intentar el WebSocket cada tanto: lo que lo impedía pudo ser pasajero. */
const WS_RETRY_WHILE_POLLING_MS = 30_000
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
 *  - si el WebSocket no abre PERO el Record sí responde por HTTP (un proxy que no deja pasar WebSocket), se recurre al
 *    sondeo de `preview.jpg`, y cada 30 s se prueba a volver. Con el Record apagado o reiniciándose NO se cambia de
 *    transporte: se sigue llamando al WebSocket hasta que vuelva (antes, un reinicio del Record dejaba el panel en
 *    sondeo HTTP para siempre).
 * `containerRef` va en el contenedor (lo que se observa para saber si está a la vista) y `canvasRef` en el `<canvas>`.
 */
export function useChannelPreview(channelId: string, enabled: boolean, fps: number) {
  const [state, setState] = useState<PreviewState>('loading')
  const [hasFrame, setHasFrame] = useState(false)
  const [stats, setStats] = useState<PreviewStats | null>(null)
  // Cambiar de Record en el panel (IP/puerto) cierra este socket y abre otro contra la dirección nueva.
  const { baseUrl } = useConnection()
  const canvas = useRef<HTMLCanvasElement | null>(null)
  // Con IntersectionObserver no se da por visible hasta que él lo diga (avisa en el primer cuadro): una tarjeta que
  // nace fuera de pantalla ya no abre un WebSocket para cerrarlo al instante.
  const inView = useRef(typeof IntersectionObserver === 'undefined')
  const observer = useRef<IntersectionObserver | null>(null)
  /** Re-evalúa si toca estar conectado (lo llaman la visibilidad de la pestaña y el IntersectionObserver). */
  const sync = useRef<() => void>(() => {})
  /** De qué Record y canal es la imagen que hay pintada (para no enseñar un cuadro de OTRO Record al cambiar de conexión). */
  const paintedFrom = useRef('')

  const containerRef = useCallback((node: HTMLElement | null) => {
    observer.current?.disconnect()
    observer.current = null
    if (typeof IntersectionObserver === 'undefined') { inView.current = true; return }
    inView.current = false // sin contenedor no hay nada a la vista; con él, lo dirá el observador en su primer aviso
    if (!node) return
    observer.current = new IntersectionObserver(
      entries => { inView.current = entries.some(e => e.isIntersecting); sync.current() },
      { rootMargin: '120px' },
    )
    observer.current.observe(node)
  }, [])
  const canvasRef = useCallback((node: HTMLCanvasElement | null) => { canvas.current = node }, [])

  useEffect(() => {
    if (!enabled) { setState('loading'); setHasFrame(false); setStats(null); paintedFrom.current = ''; return }
    // Otro Record u otro canal: la imagen anterior ya no vale. Un cambio de ritmo, en cambio, conserva el último cuadro.
    const target = `${baseUrl}|${channelId}`
    if (paintedFrom.current !== target) { paintedFrom.current = target; setState('loading'); setHasFrame(false); setStats(null) }

    let disposed = false
    let socket: WebSocket | null = null
    let reconnectTimer: number | undefined
    let connectTimer: number | undefined
    let pollTimer: number | undefined
    let upgradeTimer: number | undefined
    /** Último intento de volver del sondeo al WebSocket: la cuenta NO se reinicia cada vez que la tarjeta se oculta. */
    let lastUpgradeAt = 0
    let polling = false
    /** Hay una petición del sondeo en vuelo: otra llamada a `sync` no debe arrancar una SEGUNDA cadena de sondeo. */
    let pollInFlight = false
    let probing = false
    /** El Record acaba de responder por HTTP: el próximo intento de WebSocket es el DECISIVO (si falla, no es que esté apagado). */
    let reachable = false
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

    // Un WebSocket que no llegó a abrir. Si el Record respondía por HTTP hace un instante, no es que esté apagado: algo
    // en medio no deja pasar WebSocket → sondeo HTTP. Si no, se reintenta.
    const failedToOpen = () => {
      wsFailures++
      if (reachable) { reachable = false; polling = true; lastUpgradeAt = performance.now(); sync.current(); return }
      reconnectTimer = window.setTimeout(() => sync.current(), RECONNECT_MS)
    }

    const connect = () => {
      let s: WebSocket
      // El constructor puede LANZAR (el navegador se niega: página https con un Record http, URL inválida). Sin esto la
      // excepción moría en el temporizador y la vista previa se quedaba en «Cargando…» para siempre, sin reintentos.
      try { s = new WebSocket(wsPreviewUrl(channelId, PREVIEW_WIDTH, fps)); s.binaryType = 'blob' }
      catch { failedToOpen(); return }
      socket = s
      let opened = false
      s.onopen = () => {
        opened = true
        wsFailures = 0
        reachable = false
        // El WebSocket ha vuelto (o por fin pasa): se deja el sondeo.
        if (polling) { polling = false; window.clearTimeout(pollTimer); pollTimer = undefined; window.clearTimeout(upgradeTimer); upgradeTimer = undefined }
      }
      s.onmessage = e => {
        if (typeof e.data === 'string') { if (e.data === 'unavailable') markUnavailable(); return }
        const blob = e.data as Blob
        bytes += blob.size
        void draw(blob)
      }
      s.onclose = () => {
        if (socket !== s || disposed) return
        socket = null
        if (polling) return // era un intento de volver desde el sondeo: este sigue, y se reintentará más tarde
        if (!opened) { failedToOpen(); return }
        reconnectTimer = window.setTimeout(() => sync.current(), RECONNECT_MS)
      }
    }

    const fetchJpeg = () => api.get<Blob>(`/channels/${channelId}/preview.jpg`, {
      params: { w: PREVIEW_WIDTH }, responseType: 'blob', signal: aborter.signal, timeout: 5000,
    })

    // El WebSocket no abre. ¿Es cosa del WebSocket o es que el Record no está? Se pregunta por HTTP. Si no responde, el
    // Record está apagado o reiniciándose: se sigue llamando al WebSocket, que es lo que habrá cuando vuelva. Si RESPONDE
    // (aunque sea con un error), se hace UN intento más de WebSocket —puede que el Record acabe de volver justo ahora— y
    // solo si ese también falla se pasa al sondeo (ver `onclose`).
    const probe = async () => {
      if (probing) return
      probing = true
      try {
        const res = await fetchJpeg()
        if (disposed) return
        reachable = true
        bytes += res.data.size
        void draw(res.data)
      } catch (e) {
        if (disposed) return
        reachable = (e as { response?: unknown }).response !== undefined
        if (!reachable) markUnavailable()
      } finally { probing = false }
      wsFailures = 0
      if (!disposed) sync.current() // alcanzable → el intento decisivo, ya; si no, a seguir llamando al WebSocket
    }

    const tryUpgrade = () => {
      upgradeTimer = undefined
      if (disposed || !polling || !shouldRun()) return
      lastUpgradeAt = performance.now()
      if (!socket) connect()
      upgradeTimer = window.setTimeout(tryUpgrade, WS_RETRY_WHILE_POLLING_MS)
    }

    const poll = async () => {
      pollTimer = undefined
      if (disposed || !polling || pollInFlight) return
      pollInFlight = true
      let delay = Math.max(POLL_MIN_MS, 1000 / fps)
      try {
        const res = await fetchJpeg()
        if (disposed || !polling) return
        bytes += res.data.size
        void draw(res.data)
      } catch {
        if (disposed || !polling) return
        markUnavailable()
        delay = POLL_RETRY_MS
      } finally { pollInFlight = false }
      if (polling && shouldRun()) pollTimer = window.setTimeout(poll, delay)
    }

    const shouldRun = () => document.visibilityState === 'visible' && inView.current

    sync.current = () => {
      if (disposed) return
      window.clearTimeout(reconnectTimer)
      if (!shouldRun()) {
        closeSocket()
        window.clearTimeout(connectTimer); connectTimer = undefined
        window.clearTimeout(pollTimer); pollTimer = undefined
        window.clearTimeout(upgradeTimer); upgradeTimer = undefined
        return
      }
      if (polling) {
        if (pollTimer === undefined && !pollInFlight) void poll()
        if (upgradeTimer === undefined)
          upgradeTimer = window.setTimeout(tryUpgrade, Math.max(0, WS_RETRY_WHILE_POLLING_MS - (performance.now() - lastUpgradeAt)))
      }
      else if (socket || connectTimer !== undefined) { /* conectado, conectando o a punto de hacerlo */ }
      else if (wsFailures >= WS_FAILURES_BEFORE_PROBE) void probe()
      else connectTimer = window.setTimeout(() => {
        connectTimer = undefined
        if (!disposed && shouldRun() && !socket && !polling) connect()
      }, CONNECT_DELAY_MS)
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
      window.clearTimeout(connectTimer)
      window.clearTimeout(pollTimer)
      window.clearTimeout(upgradeTimer)
      aborter.abort()
      closeSocket()
    }
  }, [channelId, enabled, fps, baseUrl])

  useEffect(() => () => observer.current?.disconnect(), [])

  return { containerRef, canvasRef, state, hasFrame, stats }
}
