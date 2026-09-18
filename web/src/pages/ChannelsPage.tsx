import { useEffect, useMemo, useState } from 'react'
import { Box, Card, InputAdornment, Skeleton, TextField, Tooltip, Typography } from '@mui/material'
import PersonOutlineRoundedIcon from '@mui/icons-material/PersonOutlineRounded'
import VideocamOffOutlinedIcon from '@mui/icons-material/VideocamOffOutlined'
import WifiOffRoundedIcon from '@mui/icons-material/WifiOffRounded'
import { useChannels, useLicense, useRecordings } from '../hooks/queries'
import ChannelCard from '../components/ChannelCard'
import { ConnectionError, EmptyState, PageHeader } from '../components/common'
import { RecordingState } from '../api/types'
import { toneColor, toneTint } from '../theme'

const OPERATOR_KEY = 'baioss.operator'

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
  const channels = useChannels()
  const license = useLicense()
  const [operator, setOperator] = useState(() => {
    try { return localStorage.getItem(OPERATOR_KEY) ?? '' } catch { return '' }
  })
  useEffect(() => {
    try { localStorage.setItem(OPERATOR_KEY, operator) } catch { /* sin almacenamiento local: no pasa nada */ }
  }, [operator])

  const list = useMemo(() => (channels.data ?? []).slice().sort((a, b) => a.key.localeCompare(b.key)), [channels.data])

  // Inicio de las sesiones EN CURSO (sin fin), para el cronómetro de grabación de cada tarjeta.
  const recordings = useRecordings({ days: 1 })
  const startedAt = useMemo(
    () => new Map((recordings.data ?? []).filter(r => !r.endedAt).map(r => [r.id, r.startedAt])),
    [recordings.data],
  )

  const recordingCount = list.filter(c => c.recordingState === RecordingState.Recording || c.recordingState === RecordingState.Paused).length
  const subtitle = !channels.data
    ? 'Estado de cada canal, en vivo'
    : `${list.length} ${list.length === 1 ? 'canal' : 'canales'} · ${recordingCount === 0 ? 'ninguno grabando' : `${recordingCount} grabando`}`
  // La licencia solo bloquea si la aplicación dice expresamente que no se puede grabar.
  const canRecord = license.data ? license.data.canStartRecording : true

  return (
    <Box>
      <PageHeader title="Canales" subtitle={subtitle}>
        <Tooltip title="Tu nombre queda en la auditoría como quien inició la grabación.">
          <TextField
            size="small"
            placeholder="Tu nombre"
            value={operator}
            onChange={e => setOperator(e.target.value)}
            sx={{ width: 220 }}
            slotProps={{
              htmlInput: { 'aria-label': 'Operador', maxLength: 60 },
              input: { startAdornment: <InputAdornment position="start"><PersonOutlineRoundedIcon fontSize="small" /></InputAdornment> },
            }}
          />
        </Tooltip>
      </PageHeader>

      {channels.isError && channels.data && (
        <Box
          role="status"
          sx={t => ({ display: 'flex', alignItems: 'center', gap: 1.25, mb: 2, px: 2, py: 1.25, borderRadius: 3, bgcolor: toneTint(t, 'warning') })}
        >
          <WifiOffRoundedIcon sx={t => ({ fontSize: 18, color: toneColor(t, 'warning') })} />
          <Typography variant="body2">Se perdió la conexión con la aplicación. Ves los últimos datos recibidos; se reintenta solo.</Typography>
        </Box>
      )}

      {channels.isPending ? (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          <CardSkeleton /><CardSkeleton />
        </Box>
      ) : !channels.data ? (
        <ConnectionError error={channels.error} onRetry={() => void channels.refetch()} />
      ) : list.length === 0 ? (
        <Card>
          <EmptyState
            icon={<VideocamOffOutlinedIcon />}
            title="Todavía no hay canales"
            description="Los canales se configuran en la aplicación de escritorio. En cuanto tenga alguno, aparecerá aquí."
          />
        </Card>
      ) : (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          {list.map(ch => (
            <ChannelCard
              key={ch.channelId}
              ch={ch}
              operator={operator}
              canRecord={canRecord}
              recordingSince={ch.sessionId ? startedAt.get(ch.sessionId) : undefined}
            />
          ))}
        </Box>
      )}
    </Box>
  )
}
