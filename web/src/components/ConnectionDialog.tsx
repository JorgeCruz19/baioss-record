import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import axios from 'axios'
import {
  Box, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Radio, RadioGroup,
  TextField, Typography,
} from '@mui/material'
import CheckCircleOutlineRoundedIcon from '@mui/icons-material/CheckCircleOutlineRounded'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import { useQueryClient } from '@tanstack/react-query'
import { baseUrlOf, DEFAULT_PORT, parseAddress, setConnection, useConnection, type Connection } from '../api/connection'
import { toneColor, toneTint } from '../theme'
import { useSnack } from './Snack'
import { plural, useT, type Key } from '../i18n'

/** Abre el diálogo «Conexión con el Record» desde cualquier sitio (barra lateral, pantalla de «sin conexión»). */
const ConnectionUi = createContext<{ open: () => void }>({ open: () => {} })
export const useConnectionDialog = () => useContext(ConnectionUi)

type Probe = { state: 'idle' } | { state: 'testing' } | { state: 'ok'; channels: number } | { state: 'failed' }

/**
 * Proveedor + diálogo. Elegir a qué Record se conecta el panel es cosa del NAVEGADOR: se guarda aquí y se aplica en el
 * acto (consultas y WebSockets se rehacen), sin tocar archivos ni recompilar.
 */
export function ConnectionDialogProvider({ children }: { children: ReactNode }) {
  const [isOpen, setIsOpen] = useState(false)
  const open = useCallback(() => setIsOpen(true), [])
  const value = useMemo(() => ({ open }), [open])
  return (
    <ConnectionUi.Provider value={value}>
      {children}
      {isOpen && <ConnectionDialog onClose={() => setIsOpen(false)} />}
    </ConnectionUi.Provider>
  )
}

function ConnectionDialog({ onClose }: { onClose: () => void }) {
  const { t } = useT()
  const { connection } = useConnection()
  const qc = useQueryClient()
  const { notify } = useSnack()
  const [mode, setMode] = useState<'same' | 'remote'>(connection ? 'remote' : 'same')
  const [host, setHost] = useState(connection?.host ?? '')
  const [port, setPort] = useState(String(connection?.port ?? DEFAULT_PORT))
  const [error, setError] = useState<Key | null>(null)
  const [probe, setProbe] = useState<Probe>({ state: 'idle' })

  const origin = window.location.origin
  // Una página HTTPS no puede llamar a un Record HTTP: el navegador lo bloquea antes de salir (contenido mixto).
  const mixedContent = mode === 'remote' && window.location.protocol === 'https:'

  /** Lo escrito, ya interpretado: `null` = este mismo servidor; `undefined` = hay un error (ya mostrado). */
  const resolve = (): Connection | null | undefined => {
    if (mode === 'same') { setError(null); return null }
    const parsed = parseAddress(host, port)
    if ('error' in parsed) { setError(parsed.error); return undefined }
    setError(null)
    // Si pegaron «ip:puerto» o una URL en el campo de dirección, se reparte en los dos campos para que se vea.
    setHost(parsed.host); setPort(String(parsed.port))
    return parsed
  }

  const test = async () => {
    const target = resolve()
    if (target === undefined) return
    setProbe({ state: 'testing' })
    try {
      // Cliente aparte: prueba la dirección ESCRITA, no la que está en uso.
      const res = await axios.get<unknown[]>(`${baseUrlOf(target)}/api/v1/channels`, { timeout: 4000 })
      setProbe({ state: 'ok', channels: Array.isArray(res.data) ? res.data.length : 0 })
    } catch { setProbe({ state: 'failed' }) }
  }

  const save = () => {
    const target = resolve()
    if (target === undefined) return
    setConnection(target)
    // Lo que había en pantalla era de OTRO Record (o de ninguno): fuera, y se vuelve a pedir todo a la dirección nueva.
    void qc.resetQueries()
    notify(target ? t('conn.connectingRemote', { host: target.host, port: target.port }) : t('conn.connectingSame'), 'success')
    onClose()
  }

  const edited = () => { setProbe({ state: 'idle' }); setError(null) }

  return (
    <Dialog open onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{t('conn.title')}</DialogTitle>
      <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <Typography variant="body2" color="text.secondary">{t('conn.intro')}</Typography>

        <RadioGroup value={mode} onChange={e => { setMode(e.target.value as 'same' | 'remote'); edited() }}>
          <FormControlLabel
            value="same"
            control={<Radio size="small" />}
            label={
              <Box>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>{t('conn.same')}</Typography>
                <Typography variant="caption" color="text.secondary">{t('conn.sameHint')}</Typography>
              </Box>
            }
            sx={{ alignItems: 'flex-start', mb: 1, '& .MuiRadio-root': { mt: -0.5 } }}
          />
          <FormControlLabel
            value="remote"
            control={<Radio size="small" />}
            label={
              <Box>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>{t('conn.remote')}</Typography>
                <Typography variant="caption" color="text.secondary">{t('conn.remoteHint')}</Typography>
              </Box>
            }
            sx={{ alignItems: 'flex-start', '& .MuiRadio-root': { mt: -0.5 } }}
          />
        </RadioGroup>

        {mode === 'remote' && (
          <Box sx={{ display: 'flex', gap: 1.5 }}>
            <TextField
              label={t('conn.host')}
              placeholder="192.168.1.10"
              size="small"
              autoFocus
              fullWidth
              value={host}
              error={!!error}
              onChange={e => { setHost(e.target.value); edited() }}
              onKeyDown={e => { if (e.key === 'Enter') void test() }}
              slotProps={{ htmlInput: { spellCheck: false, autoCapitalize: 'none', inputMode: 'url' } }}
            />
            <TextField
              label={t('conn.port')}
              size="small"
              value={port}
              error={!!error}
              onChange={e => { setPort(e.target.value.replace(/\D/g, '').slice(0, 5)); edited() }}
              onKeyDown={e => { if (e.key === 'Enter') void test() }}
              sx={{ width: 110, flexShrink: 0 }}
              slotProps={{ htmlInput: { inputMode: 'numeric' } }}
            />
          </Box>
        )}

        {error && <Typography variant="caption" sx={th => ({ color: toneColor(th, 'critical') })}>{t(error)}</Typography>}

        {mixedContent && (
          <Typography variant="caption" sx={th => ({ p: 1.25, borderRadius: 2, bgcolor: toneTint(th, 'warning') })}>{t('conn.mixed')}</Typography>
        )}

        {probe.state === 'ok' && (
          <Box role="status" sx={th => ({ display: 'flex', alignItems: 'center', gap: 1, p: 1.25, borderRadius: 2, bgcolor: toneTint(th, 'good') })}>
            <CheckCircleOutlineRoundedIcon sx={th => ({ fontSize: 18, color: toneColor(th, 'good') })} />
            <Typography variant="body2">{t('conn.ok', { channels: plural(t, probe.channels, 'unit.channel', 'unit.channels') })}</Typography>
          </Box>
        )}
        {probe.state === 'failed' && (
          <Box role="alert" sx={th => ({ display: 'flex', gap: 1, p: 1.25, borderRadius: 2, bgcolor: toneTint(th, 'critical') })}>
            <ErrorOutlineRoundedIcon sx={th => ({ fontSize: 18, mt: 0.25, color: toneColor(th, 'critical') })} />
            <Box>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>{t('conn.failedTitle')}</Typography>
              <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 0.5 }}>
                {mode === 'same'
                  ? t('conn.failedSame')
                  : <>{t('conn.failedRemote')}{' '}<Box component="code" sx={{ fontWeight: 600, userSelect: 'all' }}>{origin}</Box></>}
              </Typography>
            </Box>
          </Box>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={() => void test()} disabled={probe.state === 'testing'} startIcon={probe.state === 'testing' ? <CircularProgress size={14} /> : undefined}>
          {t('action.test')}
        </Button>
        <Box sx={{ flexGrow: 1 }} />
        <Button onClick={onClose}>{t('action.cancel')}</Button>
        <Button variant="contained" onClick={save}>{t('action.save')}</Button>
      </DialogActions>
    </Dialog>
  )
}
