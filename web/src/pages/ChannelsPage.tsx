import { useEffect, useMemo, useState } from 'react'
import { Box, Card, FormControlLabel, InputAdornment, MenuItem, Select, Skeleton, Switch, TextField, Tooltip, Typography } from '@mui/material'
import PersonOutlineRoundedIcon from '@mui/icons-material/PersonOutlineRounded'
import VideocamOffOutlinedIcon from '@mui/icons-material/VideocamOffOutlined'
import WifiOffRoundedIcon from '@mui/icons-material/WifiOffRounded'
import { useActiveTasks, useChannels, useFirstLoad, useLicense, useRecordings } from '../hooks/queries'
import { useOperator } from '../hooks/useOperator'
import { DEFAULT_PREVIEW_RATE, PREVIEW_RATES, type PreviewRate } from '../hooks/useChannelPreview'
import ChannelCard from '../components/ChannelCard'
import { ConnectionError, EmptyState, PageHeader } from '../components/common'
import { RecordingState } from '../api/types'
import { toneColor, toneTint } from '../theme'
import { plural, useT, type Key } from '../i18n'

const PREVIEW_KEY = 'baioss.preview'
const PREVIEW_FPS_KEY = 'baioss.previewFps'

/** Lo que el operador elige es un compromiso entre fluidez y consumo: se dice en palabras, no solo con el número. */
const rateKeys: Record<PreviewRate, Key> = { 1: 'rate.1', 5: 'rate.5', 10: 'rate.10' }

function CardSkeleton() {
  return (
    <Card sx={{ p: 2.5, display: 'flex', flexDirection: 'column', gap: 2.5 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
        <Skeleton variant="rounded" width={40} height={40} sx={{ borderRadius: '12px' }} />
        <Box sx={{ flexGrow: 1 }}><Skeleton width="45%" height={20} /><Skeleton width="30%" height={14} /></Box>
        <Skeleton variant="rounded" width={86} height={26} sx={{ borderRadius: 999 }} />
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'flex-end', gap: 2 }}>
        <Box sx={{ flexGrow: 1 }}><Skeleton variant="rounded" width={84} height={26} sx={{ borderRadius: 999 }} /><Skeleton width={150} height={44} /></Box>
        <Skeleton variant="rounded" width={112} height={42} sx={{ borderRadius: '10px' }} />
      </Box>
      <Skeleton height={14} /><Skeleton height={14} width="80%" />
    </Card>
  )
}

export default function ChannelsPage() {
  const { t } = useT()
  const channels = useChannels()
  const first = useFirstLoad(channels)
  const license = useLicense()
  const [operator, setOperator] = useOperator()

  // Vista previa de baja resolución: encendida por defecto; quien vaya justo de red la apaga y se recuerda.
  const [showPreview, setShowPreview] = useState(() => {
    try { return localStorage.getItem(PREVIEW_KEY) !== '0' } catch { return true }
  })
  useEffect(() => {
    try { localStorage.setItem(PREVIEW_KEY, showPreview ? '1' : '0') } catch { /* ídem */ }
  }, [showPreview])

  // Ritmo de la vista previa: la aplicación empuja las imágenes por WebSocket a este paso.
  const [previewFps, setPreviewFps] = useState<PreviewRate>(() => {
    try {
      const saved = Number(localStorage.getItem(PREVIEW_FPS_KEY))
      return (PREVIEW_RATES as readonly number[]).includes(saved) ? (saved as PreviewRate) : DEFAULT_PREVIEW_RATE
    } catch { return DEFAULT_PREVIEW_RATE }
  })
  useEffect(() => {
    try { localStorage.setItem(PREVIEW_FPS_KEY, String(previewFps)) } catch { /* ídem */ }
  }, [previewFps])

  const list = useMemo(() => (channels.data ?? []).slice().sort((a, b) => a.key.localeCompare(b.key)), [channels.data])

  // Inicio de las sesiones EN CURSO (sin fin), para el cronómetro de grabación de cada tarjeta.
  const recordings = useRecordings({ days: 1 })
  const startedAt = useMemo(
    () => new Map((recordings.data ?? []).filter(r => !r.endedAt).map(r => [r.id, r.startedAt])),
    [recordings.data],
  )

  // La tarea automática que cada canal tiene EN MARCHA (si la hay). Las demás se gestionan en «Programación».
  const activeTasks = useActiveTasks()
  const activeByChannel = useMemo(() => new Map((activeTasks.data ?? []).map(a => [a.channelId, a])), [activeTasks.data])

  const recordingCount = list.filter(c => c.recordingState === RecordingState.Recording || c.recordingState === RecordingState.Paused).length
  const subtitle = !channels.data
    ? t('channels.subtitleIdle')
    : `${plural(t, list.length, 'unit.channel', 'unit.channels')} · ${recordingCount === 0 ? t('channels.noneRecording') : t('channels.nRecording', { n: recordingCount })}`
  // La licencia solo bloquea si la aplicación dice expresamente que no se puede grabar.
  const canRecord = license.data ? license.data.canStartRecording : true

  return (
    <Box>
      <PageHeader title={t('nav.channels')} subtitle={subtitle}>
        <Tooltip title={t('channels.previewTooltip')}>
          <FormControlLabel
            sx={{ mr: 0.5 }}
            control={<Switch size="small" checked={showPreview} onChange={e => setShowPreview(e.target.checked)} />}
            label={<Typography variant="body2">{t('channels.preview')}</Typography>}
          />
        </Tooltip>
        <Select
          size="small"
          value={previewFps}
          disabled={!showPreview}
          onChange={e => setPreviewFps(Number(e.target.value) as PreviewRate)}
          inputProps={{ 'aria-label': t('channels.previewRate') }}
          sx={{ minWidth: 176 }}
        >
          {PREVIEW_RATES.map(r => <MenuItem key={r} value={r}>{t(rateKeys[r])}</MenuItem>)}
        </Select>
        <Tooltip title={t('operator.tooltipRecord')}>
          <TextField
            size="small"
            placeholder={t('operator.placeholder')}
            value={operator}
            onChange={e => setOperator(e.target.value)}
            sx={{ width: 220 }}
            slotProps={{
              htmlInput: { 'aria-label': t('operator.aria'), maxLength: 60 },
              input: { startAdornment: <InputAdornment position="start"><PersonOutlineRoundedIcon fontSize="small" /></InputAdornment> },
            }}
          />
        </Tooltip>
      </PageHeader>

      {channels.isError && channels.data && (
        <Box
          role="status"
          sx={th => ({ display: 'flex', alignItems: 'center', gap: 1.25, mb: 2, px: 2, py: 1.25, borderRadius: 3, bgcolor: toneTint(th, 'warning') })}
        >
          <WifiOffRoundedIcon sx={th => ({ fontSize: 18, color: toneColor(th, 'warning') })} />
          <Typography variant="body2">{t('channels.lostConnection')}</Typography>
        </Box>
      )}

      {first.loading ? (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          <CardSkeleton /><CardSkeleton />
        </Box>
      ) : !channels.data ? (
        <ConnectionError error={first.error} onRetry={() => void channels.refetch()} />
      ) : list.length === 0 ? (
        <Card>
          <EmptyState icon={<VideocamOffOutlinedIcon />} title={t('channels.emptyTitle')} description={t('channels.emptyBody')} />
        </Card>
      ) : (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          {list.map(ch => (
            <ChannelCard
              key={ch.channelId}
              ch={ch}
              operator={operator}
              canRecord={canRecord}
              showPreview={showPreview}
              previewFps={previewFps}
              recordingSince={ch.sessionId ? startedAt.get(ch.sessionId) : undefined}
              activeTask={activeByChannel.get(ch.channelId)}
            />
          ))}
        </Box>
      )}
    </Box>
  )
}
