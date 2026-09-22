import { useMemo, useState, type ReactNode } from 'react'
import {
  Box, Button, Card, ListItemIcon, ListItemText, Menu, MenuItem, Select, Table, TableBody, TableCell, TableContainer, TableHead,
  TableRow, Typography,
} from '@mui/material'
import PersonOutlineRoundedIcon from '@mui/icons-material/PersonOutlineRounded'
import ScheduleRoundedIcon from '@mui/icons-material/ScheduleRounded'
import CodeRoundedIcon from '@mui/icons-material/CodeRounded'
import HelpOutlineRoundedIcon from '@mui/icons-material/HelpOutlineRounded'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import ShieldRoundedIcon from '@mui/icons-material/ShieldRounded'
import StarRoundedIcon from '@mui/icons-material/StarRounded'
import ExpandMoreRoundedIcon from '@mui/icons-material/ExpandMoreRounded'
import CheckRoundedIcon from '@mui/icons-material/CheckRounded'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import VideoLibraryOutlinedIcon from '@mui/icons-material/VideoLibraryOutlined'
import { Link } from 'react-router-dom'
import { useChannels, useFirstLoad, useRecordings, useSetProtection } from '../hooks/queries'
import { ChannelBadge, ConnectionError, EmptyState, PageHeader, RowsSkeleton, StatusTag } from '../components/common'
import {
  abnormalStop, formatBytes, formatClock, formatDay, formatDuration, protectionHint, protectionLabel, stopReasonLabel, triggerLabel,
} from '../api/format'
import type { ProtectionLevel, RecordingSummary, Trigger } from '../api/types'
import { useSnack } from '../components/Snack'
import { errorMessage } from '../api/client'
import { toneColor, type Tone } from '../theme'
import { plural, useT, type Key } from '../i18n'

const levels: ProtectionLevel[] = ['None', 'Important', 'Protected']
const periods: Array<{ days: number; label: Key }> = [
  { days: 1, label: 'period.day' },
  { days: 7, label: 'period.week' },
  { days: 30, label: 'period.month' },
  { days: 90, label: 'period.quarter' },
  { days: 365, label: 'period.year' },
]

const triggerIcon: Record<Trigger, ReactNode> = {
  Manual: <PersonOutlineRoundedIcon />,
  Scheduled: <ScheduleRoundedIcon />,
  Api: <CodeRoundedIcon />,
  Unknown: <HelpOutlineRoundedIcon />,
}

const protectionVisual: Record<ProtectionLevel, { icon: ReactNode; tone: Tone }> = {
  None: { icon: <ShieldOutlinedIcon />, tone: 'neutral' },
  Important: { icon: <StarRoundedIcon />, tone: 'warning' },
  Protected: { icon: <ShieldRoundedIcon />, tone: 'accent' },
}

/** Botón con el nivel actual; al pulsarlo se elige otro, con lo que implica cada uno. */
function ProtectionControl({ value, disabled, onChange }: { value: ProtectionLevel; disabled: boolean; onChange: (l: ProtectionLevel) => void }) {
  const { t } = useT()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const v = protectionVisual[value]
  return (
    <>
      <Button
        size="small"
        disabled={disabled}
        onClick={e => setAnchor(e.currentTarget)}
        endIcon={<ExpandMoreRoundedIcon />}
        aria-haspopup="menu"
        aria-label={t('protection.aria', { level: protectionLabel(value) })}
        sx={th => ({
          color: 'text.primary',
          fontWeight: 550,
          '& .MuiButton-startIcon svg': { fontSize: 17, color: toneColor(th, v.tone) },
          '& .MuiButton-endIcon svg': { fontSize: 16, color: 'text.disabled' },
        })}
        startIcon={v.icon}
      >
        {protectionLabel(value)}
      </Button>
      <Menu anchorEl={anchor} open={anchor !== null} onClose={() => setAnchor(null)}>
        {levels.map(l => (
          <MenuItem
            key={l}
            selected={l === value}
            onClick={() => { setAnchor(null); if (l !== value) onChange(l) }}
            sx={{ alignItems: 'flex-start', py: 1, maxWidth: 300 }}
          >
            <ListItemIcon sx={th => ({ mt: 0.25, minWidth: 30, '& svg': { fontSize: 18, color: toneColor(th, protectionVisual[l].tone) } })}>
              {protectionVisual[l].icon}
            </ListItemIcon>
            <ListItemText
              primary={protectionLabel(l)}
              secondary={protectionHint(l)}
              slotProps={{ primary: { sx: { fontSize: 13.5, fontWeight: 600 } }, secondary: { sx: { fontSize: 12, whiteSpace: 'normal' } } }}
            />
            {l === value && <CheckRoundedIcon sx={{ ml: 1, mt: 0.25, fontSize: 16, color: 'text.secondary' }} />}
          </MenuItem>
        ))}
      </Menu>
    </>
  )
}

export default function RecordingsPage() {
  const { t } = useT()
  const [channel, setChannel] = useState('')
  const [days, setDays] = useState(30)
  const channels = useChannels()
  const recordings = useRecordings({ channel, days })
  const firstLoad = useFirstLoad(recordings)
  const setProtection = useSetProtection()
  const { notify } = useSnack()

  const keyOf = useMemo(() => new Map((channels.data ?? []).map(c => [c.channelId, c.key])), [channels.data])
  const rows = recordings.data ?? []
  const totalBytes = rows.reduce((sum, r) => sum + r.totalBytes, 0)
  const period = t(periods.find(p => p.days === days)?.label ?? 'period.month').toLowerCase()

  const change = (r: RecordingSummary, level: ProtectionLevel) => setProtection.mutate({ id: r.id, level }, {
    onSuccess: () => notify(level === 'None' ? t('recordings.protectionRemoved') : t('recordings.markedAs', { level: protectionLabel(level).toLowerCase() }), 'success'),
    onError: e => notify(t('recordings.protectionFailed', { error: errorMessage(e) }), 'error'),
  })

  return (
    <Box>
      <PageHeader
        title={t('nav.recordings')}
        subtitle={recordings.data
          ? `${plural(t, rows.length, 'unit.recording', 'unit.recordings')} · ${formatBytes(totalBytes)} · ${period}`
          : t('recordings.subtitleIdle')}
      >
        <Select size="small" displayEmpty value={channel} onChange={e => setChannel(e.target.value)} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': t('filter.channel') }}>
          <MenuItem value="">{t('filter.allChannels')}</MenuItem>
          {(channels.data ?? []).map(c => <MenuItem key={c.channelId} value={c.channelId}>{t('unit.channelKey', { key: c.key })}{c.inputName ? ` · ${c.inputName}` : ''}</MenuItem>)}
        </Select>
        <Select size="small" value={days} onChange={e => setDays(Number(e.target.value))} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': t('filter.period') }}>
          {periods.map(p => <MenuItem key={p.days} value={p.days}>{t(p.label)}</MenuItem>)}
        </Select>
      </PageHeader>

      {firstLoad.loading ? (
        <Card><RowsSkeleton rows={6} /></Card>
      ) : !recordings.data ? (
        <ConnectionError error={firstLoad.error} onRetry={() => void recordings.refetch()} />
      ) : rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<VideoLibraryOutlinedIcon />}
            title={t('recordings.emptyTitle')}
            description={t('recordings.emptyBody')}
            action={<Button component={Link} to="/canales" variant="outlined">{t('recordings.goChannels')}</Button>}
          />
        </Card>
      ) : (
        <Card>
          <TableContainer>
            <Table>
              <TableHead>
                <TableRow>
                  <TableCell>{t('col.recording')}</TableCell>
                  <TableCell>{t('col.channel')}</TableCell>
                  <TableCell align="right">{t('col.duration')}</TableCell>
                  <TableCell align="right">{t('col.size')}</TableCell>
                  <TableCell>{t('col.origin')}</TableCell>
                  <TableCell>{t('col.ended')}</TableCell>
                  <TableCell>{t('col.protection')}</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map(r => {
                  const key = keyOf.get(r.channelId)
                  return (
                    <TableRow key={r.id} hover>
                      <TableCell>
                        <Typography sx={{ fontSize: 13.5, fontWeight: 600 }} noWrap>{formatDay(r.startedAt)}</Typography>
                        <Typography variant="caption" color="text.secondary" noWrap component="div" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                          {formatClock(r.startedAt)} – {r.endedAt ? formatClock(r.endedAt) : t('recordings.now')}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                          <ChannelBadge letter={key ?? '?'} size={24} />
                          {!key && <Typography variant="caption" color="text.secondary">{r.channelId.slice(0, 8)}</Typography>}
                        </Box>
                      </TableCell>
                      <TableCell align="right" sx={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{formatDuration(r.durationSeconds)}</TableCell>
                      <TableCell align="right" sx={{ whiteSpace: 'nowrap' }}>
                        <Box sx={{ fontVariantNumeric: 'tabular-nums' }}>{formatBytes(r.totalBytes)}</Box>
                        <Typography variant="caption" color="text.secondary">{plural(t, r.files, 'unit.file', 'unit.files')}</Typography>
                      </TableCell>
                      <TableCell>
                        <StatusTag label={triggerLabel(r.trigger)} icon={triggerIcon[r.trigger] ?? triggerIcon.Unknown} />
                        {r.operator && <Typography variant="caption" color="text.secondary" component="div" noWrap sx={{ mt: 0.5, maxWidth: 160 }}>{r.operator}</Typography>}
                      </TableCell>
                      <TableCell>
                        {!r.endedAt
                          ? <StatusTag tone="critical" pulsing label={t('recordings.recording')} />
                          : abnormalStop.has(r.stopReason)
                            ? <StatusTag tone="warning" icon={<WarningAmberRoundedIcon />} label={stopReasonLabel(r.stopReason)} />
                            : <Typography variant="body2" color="text.secondary">{stopReasonLabel(r.stopReason)}</Typography>}
                      </TableCell>
                      <TableCell sx={{ whiteSpace: 'nowrap' }}>
                        <ProtectionControl value={r.protection} disabled={setProtection.isPending} onChange={l => change(r, l)} />
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </TableContainer>
        </Card>
      )}
    </Box>
  )
}
