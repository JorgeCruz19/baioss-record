import { useRef, useState } from 'react'
import {
  Box, Button, Card, CircularProgress, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, Divider, TextField,
  Tooltip, Typography,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import StopRoundedIcon from '@mui/icons-material/StopRounded'
import SensorsRoundedIcon from '@mui/icons-material/SensorsRounded'
import SensorsOffRoundedIcon from '@mui/icons-material/SensorsOffRounded'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import SdStorageOutlinedIcon from '@mui/icons-material/SdStorageOutlined'
import VideocamOffOutlinedIcon from '@mui/icons-material/VideocamOffOutlined'
import TuneRoundedIcon from '@mui/icons-material/TuneRounded'
import EventRepeatRoundedIcon from '@mui/icons-material/EventRepeatRounded'
import { Link } from 'react-router-dom'
import type { ActiveTask, AudioMeter, ChannelStatus } from '../api/types'
import { RecordingState, SessionTrigger, SignalState } from '../api/types'
import {
  alarmLabel, formatBitrate, formatBytes, formatDuration, formatFps, formatInt, formatTimeSpan, recordingStateLabel, signalLabel,
} from '../api/format'
import { ChannelBadge, Meter, Metric, StatusTag } from './common'
import { useSkipScheduled, useStartRecording, useStopRecording } from '../hooks/queries'
import { remainingLabel, scheduleProblem, wallTime } from '../api/schedule'
import { useChannelPreview } from '../hooks/useChannelPreview'
import { useSnack } from './Snack'
import { errorMessage } from '../api/client'
import { toneColor, toneTint, type Tone } from '../theme'
import { useT, type Key, type T } from '../i18n'

/** -60 dBFS → 0 %, 0 dBFS → 100 % (misma escala que los medidores de la aplicación). */
const meterPercent = (db: number) => Math.max(0, Math.min(100, ((db + 60) / 60) * 100))
/** Severidad del relleno: recorte → crítico; a menos de 6 dB del techo → aviso; si no, acento. */
const meterTone = (m: AudioMeter): Tone => (m.clipping ? 'critical' : m.peakDb > -6 ? 'warning' : 'accent')
/** Etiqueta sobre la imagen de vista previa (abajo): fondo translúcido para leerse sobre cualquier vídeo. */
const previewChip = { position: 'absolute', bottom: 8, px: 1, py: 0.25, borderRadius: 1, bgcolor: 'rgba(0,0,0,.6)', color: '#fff' } as const
const formatDb = (db: number) => `${db <= -60 ? '−∞' : db.toFixed(1)} dB`

/** Tope del nombre, el mismo que aplica la API. */
const NAME_MAX = 120
/** Lo que Windows no admite en un nombre de archivo (y el «%», que la aplicación también quita). */
const INVALID_NAME_CHARS = /[\\/:*?"<>|%]/g
/** El mismo nombre que propone la aplicación de escritorio al detener: «Grabación 19-09-2026». */
const suggestedName = (t: T) => {
  const d = new Date()
  const two = (n: number) => String(n).padStart(2, '0')
  return t('card.suggestedName', { date: `${two(d.getDate())}-${two(d.getMonth() + 1)}-${d.getFullYear()}` })
}
/** Por qué la aplicación no aplicó el nombre pedido (campo `detail` de la respuesta de «detener»). */
const renameIgnored: Record<string, Key> = {
  'not-recording': 'rename.notRecording',
  scheduled: 'rename.scheduled',
  unsupported: 'rename.unsupported',
  'not-renamed': 'rename.notRenamed',
}

interface Props {
  ch: ChannelStatus
  operator: string
  /** Inicio (ISO) de la sesión en curso según GET /recordings, para el cronómetro de grabación. */
  recordingSince?: string
  /** La licencia permite grabar (si no, el botón se desactiva y lo explica). */
  canRecord: boolean
  /** Mostrar la vista previa de baja resolución (el operador puede apagarla para no gastar red). */
  showPreview: boolean
  /** Imágenes por segundo de la vista previa (1 = mínimo consumo, 10 = fluido). */
  previewFps: number
  /** La tarea automática que este canal tiene EN MARCHA ahora mismo, si la hay (las demás están en «Programación»). */
  activeTask?: ActiveTask
}

export default function ChannelCard({ ch, operator, recordingSince, canRecord, showPreview, previewFps, activeTask }: Props) {
  const { t } = useT()
  const { notify } = useSnack()
  const start = useStartRecording()
  const stop = useStopRecording()
  const skip = useSkipScheduled()
  const [confirmStop, setConfirmStop] = useState(false)
  const [fileName, setFileName] = useState('')
  const nameInput = useRef<HTMLInputElement>(null)
  const preview = useChannelPreview(ch.channelId, showPreview, previewFps)
  // Una grabación PROGRAMADA ya se guarda como fecha_Título: no se le pide otro nombre (la API lo ignoraría igualmente).
  const scheduled = ch.sessionTrigger === SessionTrigger.Scheduled

  const recording = ch.recordingState === RecordingState.Recording
    || ch.recordingState === RecordingState.Starting
    || ch.recordingState === RecordingState.Recovering
    || ch.recordingState === RecordingState.Paused
  const busy = start.isPending || stop.isPending || skip.isPending
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
    ?? (ch.signal.resolution ? `${ch.signal.resolution.width}×${ch.signal.resolution.height} · ${formatFps(ch.signal.frameRate)} fps` : t('card.formatUnknown'))
  // Con una fuente de 8/16 canales se indica qué par se graba («Par 3-4 de 8»); con estéreo no hay nada que decir.
  const format = ch.signal.audioSelectionLabel ? `${video} · ${ch.signal.audioSelectionLabel}` : video
  const name = ch.inputName ?? t('unit.channelKey', { key: ch.key })

  const stateTone: Tone = recording ? 'critical' : ch.recordingState === RecordingState.Error ? 'warning' : 'neutral'
  const signalTone: Tone = ch.signal.state === SignalState.Locked ? 'good' : ch.signal.state === SignalState.Unstable ? 'warning' : 'critical'

  const onStart = () => start.mutate({ id: ch.channelId, operator: operator.trim() || null }, {
    onSuccess: () => notify(t('card.recording', { key: ch.key }), 'success'),
    onError: e => notify(t('card.startFailed', { key: ch.key, error: errorMessage(e) }), 'error'),
  })
  const askStop = () => { setFileName(scheduled ? '' : suggestedName(t)); setConfirmStop(true) }
  const cleanName = fileName.replace(INVALID_NAME_CHARS, '').trim()
  const stopPlain = (name: string | null) => stop.mutate({ id: ch.channelId, name, operator: operator.trim() || null }, {
    onSuccess: () => notify(t('card.saved', { key: ch.key }), 'success'),
    onError: e => notify(t('card.stopFailed', { key: ch.key, error: errorMessage(e) }), 'error'),
  })
  const onStop = () => {
    setConfirmStop(false)
    // Una grabación AUTOMÁTICA no se «detiene» sin más: se SALTA esa ocurrencia (como el ⏏ de la aplicación). Así el Record
    // sabe que fue una decisión del operador, no la reanuda, y la tarea sigue activa para las próximas veces.
    if (scheduled) {
      skip.mutate(ch.channelId, {
        onSuccess: () => notify(t('card.skipped', { key: ch.key }), 'success'),
        onError: e => {
          // El Record ya no la lleva como tarea en marcha (p. ej. la borraron mientras grababa): se detiene sin más.
          if (scheduleProblem(e).code === 'no-active-task') stopPlain(null)
          else notify(t('card.stopFailed', { key: ch.key, error: scheduleProblem(e).message }), 'error')
        },
      })
      return
    }
    const name = cleanName || null
    stop.mutate({ id: ch.channelId, name, operator: operator.trim() || null }, {
      onSuccess: r => {
        if (r?.renamed) notify(t('card.savedAs', { key: ch.key, name: r.fileName ?? '' }), 'success')
        else if (r?.pending) notify(t('card.savedPending', { key: ch.key, name: name ?? '' }), 'success')
        else if (r) notify(t('card.savedAuto', { key: ch.key, why: t(renameIgnored[r.detail ?? ''] ?? 'rename.failed') }), 'warning')
        else notify(t('card.saved', { key: ch.key }), 'success')
      },
      onError: e => notify(t('card.stopFailed', { key: ch.key, error: errorMessage(e) }), 'error'),
    })
  }

  return (
    <Card
      component="article"
      aria-label={t('card.aria', { key: ch.key, name })}
      sx={th => ({
        display: 'flex',
        flexDirection: 'column',
        gap: 2.5,
        p: 2.5,
        transition: 'border-color .25s ease, box-shadow .25s ease',
        ...(recording && {
          borderColor: alpha(toneColor(th, 'critical'), 0.55),
          boxShadow: `0 0 0 4px ${alpha(toneColor(th, 'critical'), 0.1)}`,
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
          label={signalLabel(ch.signal.state)}
          icon={ch.signal.state === SignalState.NoSignal ? <SensorsOffRoundedIcon /> : <SensorsRoundedIcon />}
        />
      </Box>

      {/* Vista previa de baja resolución: la aplicación empuja JPEG por WebSocket, solo con la tarjeta a la vista */}
      {showPreview && (
        <Box
          ref={preview.containerRef}
          sx={th => ({
            position: 'relative',
            aspectRatio: '16 / 9',
            width: '100%',
            overflow: 'hidden',
            borderRadius: 2.5,
            bgcolor: th.palette.mode === 'dark' ? '#0b0d12' : '#11151c', // la imagen es vídeo: fondo oscuro en ambos temas
            display: 'grid',
            placeItems: 'center',
          })}
        >
          {/* Se pinta por referencia (canvas): un cuadro nuevo no re-renderiza la tarjeta. */}
          <Box
            component="canvas"
            ref={preview.canvasRef}
            role="img"
            aria-label={t('card.previewAria', { key: ch.key })}
            sx={{ width: '100%', height: '100%', objectFit: 'contain', display: preview.hasFrame ? 'block' : 'none' }}
          />
          {!preview.hasFrame && (
            <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 0.75, color: 'rgba(255,255,255,.62)', px: 2, textAlign: 'center' }}>
              {preview.state === 'unavailable'
                ? <VideocamOffOutlinedIcon sx={{ fontSize: 28 }} />
                : <CircularProgress size={22} sx={{ color: 'rgba(255,255,255,.62)' }} />}
              <Typography variant="caption" sx={{ color: 'inherit' }}>
                {t(preview.state === 'unavailable' ? 'card.previewUnavailable' : 'card.previewLoading')}
              </Typography>
            </Box>
          )}
          {preview.hasFrame && preview.state === 'unavailable' && (
            <Typography variant="caption" sx={{ ...previewChip, left: 8 }}>{t('card.previewStale')}</Typography>
          )}
          {/* Lo que cuesta de verdad, medido en el navegador: ritmo recibido y consumo de red de este canal. */}
          {preview.stats && preview.state === 'live' && (
            <Typography variant="caption" sx={{ ...previewChip, right: 8, fontVariantNumeric: 'tabular-nums' }}>
              {preview.stats.fps.toFixed(preview.stats.fps < 3 ? 1 : 0)} img/s · {Math.round(preview.stats.kbps)} kbps
              {preview.stats.transport === 'http' ? ' · HTTP' : ''}
            </Typography>
          )}
        </Box>
      )}

      {/* Estado + cronómetro, y la acción principal */}
      <Box sx={{ display: 'flex', alignItems: 'flex-end', gap: 2 }}>
        <Box sx={{ flexGrow: 1, minWidth: 0 }}>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 0.75 }}>
            <StatusTag tone={stateTone} pulsing={recording} label={recordingStateLabel(ch.recordingState)} />
            {/* Con qué se grabará: no es un estado (va en neutro, con su icono), pero el operador debe verlo antes de grabar. */}
            {ch.presetName && (
              <Tooltip
                title={
                  <>
                    {ch.profileSummary ? t('card.presetTipWith', { summary: ch.profileSummary }) : t('card.presetTip')}
                    <br />{t('card.presetTipWhere')}
                  </>
                }
              >
                <Box component="span" sx={{ display: 'inline-flex', minWidth: 0, maxWidth: '100%' }}>
                  <StatusTag tone="neutral" icon={<TuneRoundedIcon />} label={ch.presetName} />
                </Box>
              </Tooltip>
            )}
          </Box>
          <Typography
            component="div"
            aria-label={recording ? t('card.elapsedAria', { time: elapsed }) : undefined}
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
            onClick={askStop}
            startIcon={busy ? <CircularProgress size={14} color="inherit" /> : <StopRoundedIcon />}
            sx={th => ({
              bgcolor: 'text.primary',
              color: 'background.paper',
              '&:hover': { bgcolor: alpha(th.palette.text.primary, 0.85) },
            })}
          >
            {t('action.stop')}
          </Button>
        ) : (
          <Tooltip title={canRecord ? '' : t('card.licenseBlocked')}>
            <span>
              <Button
                size="large"
                variant="contained"
                color="error"
                disabled={busy || !canRecord}
                onClick={onStart}
                startIcon={busy ? <CircularProgress size={14} color="inherit" /> : <FiberManualRecordIcon />}
              >
                {t('action.record')}
              </Button>
            </span>
          </Tooltip>
        )}
      </Box>

      {/* La tarea automática EN MARCHA de este canal (solo esa: el resto se gestiona en «Programación»). */}
      {activeTask && (
        <Box
          role="status"
          sx={th => ({ display: 'flex', alignItems: 'center', gap: 1.25, px: 1.5, py: 1.25, borderRadius: 2.5, bgcolor: toneTint(th, 'accent') })}
        >
          <EventRepeatRoundedIcon sx={th => ({ fontSize: 20, flexShrink: 0, color: toneColor(th, 'accent') })} />
          <Box sx={{ minWidth: 0, flexGrow: 1 }}>
            <Typography variant="caption" color="text.secondary" component="div">{t('card.activeTask')}</Typography>
            <Typography sx={{ fontSize: 14, fontWeight: 600 }} noWrap>{activeTask.title}</Typography>
            <Typography variant="caption" color="text.secondary" component="div" sx={{ fontVariantNumeric: 'tabular-nums' }}>
              {wallTime(activeTask.startedAt)} – {wallTime(activeTask.endsAt)} · {remainingLabel(activeTask.remainingSeconds)}
            </Typography>
          </Box>
          <Button component={Link} to="/programacion" size="small" sx={{ flexShrink: 0 }}>{t('card.viewTasks')}</Button>
        </Box>
      )}

      {alarms.length > 0 && (
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.75 }}>
          {alarms.map(a => (
            <Tooltip key={`${a.type}-${a.since}`} title={a.message}>
              <span>
                <StatusTag
                  tone={a.isCritical ? 'critical' : 'warning'}
                  icon={a.isCritical ? <ErrorOutlineRoundedIcon /> : <WarningAmberRoundedIcon />}
                  label={alarmLabel(a.type, a.message)}
                />
              </span>
            </Tooltip>
          ))}
        </Box>
      )}

      <Divider />

      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 2 }}>
        {/* Etiquetas cortas (caben también en el móvil); la explicación va en el título de cada cifra. */}
        <Metric label={t('metric.fps')} hint={t('metric.fpsHint')} value={ch.stats.outputFps ? ch.stats.outputFps.toFixed(1) : '—'} />
        <Metric label={t('metric.bitrate')} hint={t('metric.bitrateHint')} value={recording ? formatBitrate(ch.stats.bitrate?.bitsPerSecond ?? 0) : '—'} />
        <Metric
          label={t('metric.dropped')}
          hint={t('metric.droppedHint')}
          value={
            <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75 }}>
              {formatInt(dropped)}
              {dropped > 0 && <WarningAmberRoundedIcon sx={th => ({ fontSize: 16, color: toneColor(th, 'warning') })} />}
            </Box>
          }
        />
      </Box>

      <Box>
        <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 1 }}>
          {pairs.length > 0 ? t('audio.titleN', { n: allMeters.length }) : t('audio.title')}
        </Typography>
        {pairs.length === 0 ? (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
            {meters.map((m, i) => (
              <Box key={i} sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
                <Typography variant="caption" color="text.secondary" sx={{ width: 10 }}>{meters.length > 1 ? (i === 0 ? 'L' : 'R') : ''}</Typography>
                <Meter
                  value={meterPercent(m.peakDb)}
                  tone={meterTone(m)}
                  label={t(i === 0 ? 'audio.left' : 'audio.right')}
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
                      {p.recorded && <FiberManualRecordIcon aria-hidden sx={th => ({ fontSize: 8, color: toneColor(th, 'critical') })} />}
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
                  <Meter value={meterPercent(p.left.peakDb)} tone={meterTone(p.left)} height={4} label={t('audio.channelLevel', { n: p.pair * 2 - 1 })} />
                  <Meter value={meterPercent(p.right.peakDb)} tone={meterTone(p.right)} height={4} label={t('audio.channelLevel', { n: p.pair * 2 })} />
                </Box>
              ))}
            </Box>
            <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 1, display: 'flex', alignItems: 'center', gap: 0.5 }}>
              <FiberManualRecordIcon aria-hidden sx={th => ({ fontSize: 8, color: toneColor(th, 'critical') })} />
              {t('audio.recordedLegend')}
            </Typography>
          </>
        )}
      </Box>

      {ch.storage && ch.storage.totalBytes > 0 && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, color: 'text.secondary' }}>
          <SdStorageOutlinedIcon sx={{ fontSize: 16 }} />
          <Typography variant="caption" color="text.secondary">
            {t('storage.free', { bytes: formatBytes(ch.storage.freeBytes) })}
            {recording && ch.storage.estimatedRemaining ? ` · ${t('card.remaining', { time: formatTimeSpan(ch.storage.estimatedRemaining) })}` : ''}
            {recording && ch.stats.recordedBytes ? ` · ${t('card.recorded', { bytes: formatBytes(ch.stats.recordedBytes) })}` : ''}
          </Typography>
        </Box>
      )}

      <Dialog
        open={confirmStop}
        onClose={() => setConfirmStop(false)}
        maxWidth="xs"
        fullWidth
        // Al abrirse, el nombre sugerido queda seleccionado: escribir lo sustituye y Enter detiene (como en la aplicación).
        slotProps={{ transition: { onEntered: () => { nameInput.current?.focus(); nameInput.current?.select() } } }}
      >
        <DialogTitle>
          {scheduled
            ? activeTask ? t('stop.titleAutoNamed', { title: activeTask.title }) : t('stop.titleAuto')
            : t('stop.title', { key: ch.key })}
        </DialogTitle>
        <DialogContent>
          <DialogContentText>{t('stop.body', { elapsed })}</DialogContentText>
          {scheduled ? (
            <DialogContentText variant="body2" sx={{ mt: 1.5 }}>
              {t('stop.autoBody', { ends: activeTask ? t('stop.autoEnds', { time: wallTime(activeTask.endsAt) }) : '' })}
            </DialogContentText>
          ) : (
            <TextField
              autoFocus
              fullWidth
              size="small"
              margin="normal"
              inputRef={nameInput}
              label={t('stop.fileName')}
              value={fileName}
              onChange={e => setFileName(e.target.value.replace(INVALID_NAME_CHARS, ''))}
              onFocus={e => e.target.select()}
              onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); onStop() } }}
              helperText={cleanName ? t('stop.nameHint') : t('stop.nameEmptyHint', { key: ch.key })}
              slotProps={{ htmlInput: { maxLength: NAME_MAX, 'aria-label': t('stop.fileName') } }}
            />
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmStop(false)}>{t('stop.keep')}</Button>
          <Button variant="contained" color="error" onClick={onStop} startIcon={<StopRoundedIcon />}>{t('stop.confirm')}</Button>
        </DialogActions>
      </Dialog>
    </Card>
  )
}
