import { useState } from 'react'
import {
  Box, Button, Card, CircularProgress, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, Divider, Tooltip,
  Typography,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import StopRoundedIcon from '@mui/icons-material/StopRounded'
import SensorsRoundedIcon from '@mui/icons-material/SensorsRounded'
import SensorsOffRoundedIcon from '@mui/icons-material/SensorsOffRounded'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import SdStorageOutlinedIcon from '@mui/icons-material/SdStorageOutlined'
import type { AudioMeter, ChannelStatus } from '../api/types'
import { RecordingState, SignalState } from '../api/types'
import { alarmLabel, formatBitrate, formatBytes, formatDuration, formatFps, formatTimeSpan, recordingStateLabel, signalLabel } from '../api/format'
import { ChannelBadge, Meter, Metric, StatusTag } from './common'
import { useStartRecording, useStopRecording } from '../hooks/queries'
import { useSnack } from './Snack'
import { errorMessage } from '../api/client'
import { toneColor, type Tone } from '../theme'

/** -60 dBFS → 0 %, 0 dBFS → 100 % (misma escala que los medidores de la aplicación). */
const meterPercent = (db: number) => Math.max(0, Math.min(100, ((db + 60) / 60) * 100))
/** Severidad del relleno: recorte → crítico; a menos de 6 dB del techo → aviso; si no, acento. */
const meterTone = (m: AudioMeter): Tone => (m.clipping ? 'critical' : m.peakDb > -6 ? 'warning' : 'accent')
const formatDb = (db: number) => `${db <= -60 ? '−∞' : db.toFixed(1)} dB`

interface Props {
  ch: ChannelStatus
  operator: string
  /** Inicio (ISO) de la sesión en curso según GET /recordings, para el cronómetro de grabación. */
  recordingSince?: string
  /** La licencia permite grabar (si no, el botón se desactiva y lo explica). */
  canRecord: boolean
}

export default function ChannelCard({ ch, operator, recordingSince, canRecord }: Props) {
  const { notify } = useSnack()
  const start = useStartRecording()
  const stop = useStopRecording()
  const [confirmStop, setConfirmStop] = useState(false)

  const recording = ch.recordingState === RecordingState.Recording
    || ch.recordingState === RecordingState.Starting
    || ch.recordingState === RecordingState.Recovering
    || ch.recordingState === RecordingState.Paused
  const busy = start.isPending || stop.isPending
    || ch.recordingState === RecordingState.Starting
    || ch.recordingState === RecordingState.Stopping

  // Cronómetro de GRABACIÓN, como en la aplicación de escritorio. El timecode que reporta la API (stats.timecode) es
  // el del proceso FFmpeg de preview, que corre con -progress también en reposo: avanzaría sin estar grabando. Aquí
  // se muestra el tiempo desde el inicio de la sesión (00:00:00 en reposo); mientras /recordings no la devuelve
  // todavía (primeros segundos), se usa el timecode del proceso nuevo, que arranca de cero al grabar.
  const tc = ch.stats.timecode
  const elapsed = !recording
    ? '00:00:00'
    : recordingSince
      ? formatDuration((Date.now() - new Date(recordingSince).getTime()) / 1000)
      : formatDuration((tc?.hours ?? 0) * 3600 + (tc?.minutes ?? 0) * 60 + (tc?.seconds ?? 0))
  // Los cuadros perdidos también son de la grabación: en reposo se muestran a 0, como en la aplicación.
  const dropped = recording ? ch.stats.droppedFrames ?? 0 : 0

  const alarms = ch.alarms ?? []
  // Un medidor por canal capturado. Con estéreo, los dos de siempre (L/R); con una fuente de 8/16 canales se agrupan
  // por pares y se marca cuáles van al archivo (signal.audioSelectedPairs): el resto solo se mide.
  const allMeters = ch.audio ?? []
  const meters = allMeters.length > 0 ? allMeters.slice(0, 2) : [{ peakDb: -60, rmsDb: -60, clipping: false }]
  const selectedPairs = ch.signal.audioSelectedPairs ?? []
  const pairs = allMeters.length > 2
    ? Array.from({ length: Math.floor(allMeters.length / 2) }, (_, i) => {
        const left = allMeters[i * 2]
        const right = allMeters[i * 2 + 1]
        return {
          pair: i + 1,
          label: `${i * 2 + 1}-${i * 2 + 2}`,
          left,
          right,
          peakDb: Math.max(left.peakDb, right.peakDb),
          recorded: selectedPairs.includes(i + 1),
        }
      })
    : []
  const video = ch.signal.formatLabel
    ?? (ch.signal.resolution ? `${ch.signal.resolution.width}×${ch.signal.resolution.height} · ${formatFps(ch.signal.frameRate)} fps` : 'Formato sin detectar')
  // Con una fuente de 8/16 canales se indica qué par se graba («Par 3-4 de 8»); con estéreo no hay nada que decir.
  const format = ch.signal.audioSelectionLabel ? `${video} · ${ch.signal.audioSelectionLabel}` : video
  const name = ch.inputName ?? `Canal ${ch.key}`

  const stateTone: Tone = recording ? 'critical' : ch.recordingState === RecordingState.Error ? 'warning' : 'neutral'
  const signalTone: Tone = ch.signal.state === SignalState.Locked ? 'good' : ch.signal.state === SignalState.Unstable ? 'warning' : 'critical'

  const onStart = () => start.mutate({ id: ch.channelId, operator: operator.trim() || null }, {
    onSuccess: () => notify(`Canal ${ch.key}: grabando.`, 'success'),
    onError: e => notify(`No se pudo grabar el canal ${ch.key}. ${errorMessage(e)}`, 'error'),
  })
  const onStop = () => {
    setConfirmStop(false)
    stop.mutate(ch.channelId, {
      onSuccess: () => notify(`Canal ${ch.key}: grabación guardada.`, 'success'),
      onError: e => notify(`No se pudo detener el canal ${ch.key}. ${errorMessage(e)}`, 'error'),
    })
  }

  return (
    <Card
      component="article"
      aria-label={`Canal ${ch.key}: ${name}`}
      sx={t => ({
        display: 'flex',
        flexDirection: 'column',
        gap: 2.5,
        p: 2.5,
        transition: 'border-color .25s ease, box-shadow .25s ease',
        ...(recording && {
          borderColor: alpha(toneColor(t, 'critical'), 0.55),
          boxShadow: `0 0 0 4px ${alpha(toneColor(t, 'critical'), 0.1)}`,
        }),
      })}
    >
      {/* Quién es el canal y cómo está su señal */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
        <ChannelBadge letter={ch.key} />
        <Box sx={{ minWidth: 0, flexGrow: 1 }}>
          <Typography variant="h3" noWrap>{name}</Typography>
          <Typography variant="caption" color="text.secondary" noWrap component="div">{format}</Typography>
        </Box>
        <StatusTag
          tone={signalTone}
          label={signalLabel[ch.signal.state] ?? 'Señal'}
          icon={ch.signal.state === SignalState.NoSignal ? <SensorsOffRoundedIcon /> : <SensorsRoundedIcon />}
        />
      </Box>

      {/* Estado + cronómetro, y la acción principal */}
      <Box sx={{ display: 'flex', alignItems: 'flex-end', gap: 2 }}>
        <Box sx={{ flexGrow: 1, minWidth: 0 }}>
          <StatusTag tone={stateTone} pulsing={recording} label={recordingStateLabel[ch.recordingState] ?? 'Estado'} />
          <Typography
            component="div"
            aria-label={recording ? `Tiempo de grabación ${elapsed}` : undefined}
            sx={{
              mt: 1,
              fontSize: 32,
              fontWeight: 650,
              lineHeight: 1.1,
              letterSpacing: '-0.02em',
              fontVariantNumeric: 'tabular-nums', // un reloj que corre: dígitos de ancho fijo para que no baile
              color: recording ? 'text.primary' : 'text.disabled',
            }}
          >
            {elapsed}
          </Typography>
        </Box>

        {recording ? (
          <Button
            size="large"
            variant="contained"
            disabled={busy}
            onClick={() => setConfirmStop(true)}
            startIcon={busy ? <CircularProgress size={14} color="inherit" /> : <StopRoundedIcon />}
            sx={t => ({
              bgcolor: 'text.primary',
              color: 'background.paper',
              '&:hover': { bgcolor: alpha(t.palette.text.primary, 0.85) },
            })}
          >
            Detener
          </Button>
        ) : (
          <Tooltip title={canRecord ? '' : 'La licencia de este equipo no permite grabar.'}>
            <span>
              <Button
                size="large"
                variant="contained"
                color="error"
                disabled={busy || !canRecord}
                onClick={onStart}
                startIcon={busy ? <CircularProgress size={14} color="inherit" /> : <FiberManualRecordIcon />}
              >
                Grabar
              </Button>
            </span>
          </Tooltip>
        )}
      </Box>

      {alarms.length > 0 && (
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.75 }}>
          {alarms.map(a => (
            <Tooltip key={`${a.type}-${a.since}`} title={a.message}>
              <span>
                <StatusTag
                  tone={a.isCritical ? 'critical' : 'warning'}
                  icon={a.isCritical ? <ErrorOutlineRoundedIcon /> : <WarningAmberRoundedIcon />}
                  label={alarmLabel[a.type] ?? a.message}
                />
              </span>
            </Tooltip>
          ))}
        </Box>
      )}

      <Divider />

      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 2 }}>
        {/* Etiquetas cortas (caben también en el móvil); la explicación va en el título de cada cifra. */}
        <Metric label="FPS" hint="Cuadros por segundo que entrega la fuente" value={ch.stats.outputFps ? ch.stats.outputFps.toFixed(1) : '—'} />
        <Metric label="Bitrate" hint="Caudal real de la grabación en disco" value={recording ? formatBitrate(ch.stats.bitrate?.bitsPerSecond ?? 0) : '—'} />
        <Metric
          label="Perdidos"
          hint="Cuadros perdidos durante esta grabación"
          value={
            <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75 }}>
              {dropped.toLocaleString('es-ES')}
              {dropped > 0 && <WarningAmberRoundedIcon sx={t => ({ fontSize: 16, color: toneColor(t, 'warning') })} />}
            </Box>
          }
        />
      </Box>

      <Box>
        <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1 }}>
          {pairs.length > 0 ? `Audio · ${allMeters.length} canales de la fuente` : 'Audio'}
        </Typography>
        {pairs.length === 0 ? (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
            {meters.map((m, i) => (
              <Box key={i} sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
                <Typography variant="caption" color="text.secondary" sx={{ width: 10 }}>{meters.length > 1 ? (i === 0 ? 'L' : 'R') : ''}</Typography>
                <Meter
                  value={meterPercent(m.peakDb)}
                  tone={meterTone(m)}
                  label={`Nivel de audio ${i === 0 ? 'izquierdo' : 'derecho'}`}
                />
                <Typography variant="caption" sx={{ width: 58, textAlign: 'right', fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>
                  {formatDb(m.peakDb)}
                </Typography>
              </Box>
            ))}
          </Box>
        ) : (
          <>
            <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(112px, 1fr))', gap: 1.25 }}>
              {pairs.map(p => (
                <Box key={p.pair} sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
                  <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 0.5 }}>
                    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5 }}>
                      {p.recorded && <FiberManualRecordIcon aria-hidden sx={t => ({ fontSize: 8, color: toneColor(t, 'critical') })} />}
                      <Typography
                        variant="caption"
                        sx={{ fontWeight: p.recorded ? 700 : 500, color: p.recorded ? 'text.primary' : 'text.secondary' }}
                      >
                        {p.label}
                      </Typography>
                    </Box>
                    <Typography variant="caption" color="text.secondary" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                      {formatDb(p.peakDb)}
                    </Typography>
                  </Box>
                  <Meter value={meterPercent(p.left.peakDb)} tone={meterTone(p.left)} height={4} label={`Nivel del canal ${p.pair * 2 - 1}`} />
                  <Meter value={meterPercent(p.right.peakDb)} tone={meterTone(p.right)} height={4} label={`Nivel del canal ${p.pair * 2}`} />
                </Box>
              ))}
            </Box>
            <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 1, display: 'flex', alignItems: 'center', gap: 0.5 }}>
              <FiberManualRecordIcon aria-hidden sx={t => ({ fontSize: 8, color: toneColor(t, 'critical') })} />
              se graba · el resto solo se mide
            </Typography>
          </>
        )}
      </Box>

      {ch.storage && ch.storage.totalBytes > 0 && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, color: 'text.secondary' }}>
          <SdStorageOutlinedIcon sx={{ fontSize: 16 }} />
          <Typography variant="caption" color="text.secondary">
            {formatBytes(ch.storage.freeBytes)} libres
            {recording && ch.storage.estimatedRemaining ? ` · quedan ≈ ${formatTimeSpan(ch.storage.estimatedRemaining)}` : ''}
            {recording && ch.stats.recordedBytes ? ` · ${formatBytes(ch.stats.recordedBytes)} grabados` : ''}
          </Typography>
        </Box>
      )}

      <Dialog open={confirmStop} onClose={() => setConfirmStop(false)} maxWidth="xs" fullWidth>
        <DialogTitle>¿Detener la grabación del canal {ch.key}?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Lleva {elapsed} grabando. Al detenerla se cierra el archivo y queda guardado en la carpeta del canal.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmStop(false)}>Seguir grabando</Button>
          <Button variant="contained" color="error" onClick={onStop} startIcon={<StopRoundedIcon />}>Detener grabación</Button>
        </DialogActions>
      </Dialog>
    </Card>
  )
}
