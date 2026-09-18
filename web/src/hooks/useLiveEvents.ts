import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { wsEventsUrl } from '../api/client'

/**
 * Se suscribe al WebSocket de eventos del Record (/ws/events). Los mensajes no llevan el tipo de evento (solo sus
 * campos), así que no se interpretan: cualquier evento significa «algo cambió» y se invalidan las consultas
 * afectadas, agrupando ráfagas. El sondeo periódico de React Query sigue siendo la red de seguridad si el
 * WebSocket no está disponible. Reconecta sola con retroceso exponencial (máx. 30 s).
 */
export function useLiveEvents(): { connected: boolean } {
  const qc = useQueryClient()
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    let ws: WebSocket | null = null
    let reconnectTimer: number | undefined
    let flushTimer: number | undefined
    let retry = 0
    let closed = false

    const invalidate = () => {
      if (flushTimer !== undefined) return
      flushTimer = window.setTimeout(() => {
        flushTimer = undefined
        void qc.invalidateQueries({ queryKey: ['channels'] })
        void qc.invalidateQueries({ queryKey: ['recordings'] })
        void qc.invalidateQueries({ queryKey: ['events'] })
        void qc.invalidateQueries({ queryKey: ['storage'] })
      }, 300)
    }

    const schedule = () => {
      if (closed) return
      const delay = Math.min(30_000, 1000 * 2 ** Math.min(retry++, 5))
      reconnectTimer = window.setTimeout(connect, delay)
    }

    const connect = () => {
      if (closed) return
      try { ws = new WebSocket(wsEventsUrl()) } catch { schedule(); return }
      ws.onopen = () => { retry = 0; setConnected(true) }
      ws.onmessage = invalidate
      ws.onclose = () => { setConnected(false); schedule() }
      ws.onerror = () => { ws?.close() }
    }

    connect()
    return () => {
      closed = true
      window.clearTimeout(reconnectTimer)
      window.clearTimeout(flushTimer)
      ws?.close()
    }
  }, [qc])

  return { connected }
}
