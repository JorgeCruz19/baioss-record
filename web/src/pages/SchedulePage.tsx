import { useMemo, useState } from 'react'
import {
  Box, Button, Card, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, Divider, IconButton, InputAdornment,
  MenuItem, Select, Switch, TextField, Tooltip, Typography,
} from '@mui/material'
import AddRoundedIcon from '@mui/icons-material/AddRounded'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import DeleteOutlineRoundedIcon from '@mui/icons-material/DeleteOutlineRounded'
import EventRepeatRoundedIcon from '@mui/icons-material/EventRepeatRounded'
import PersonOutlineRoundedIcon from '@mui/icons-material/PersonOutlineRounded'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import { useChannels, useDeleteTask, useFirstLoad, useSchedule, useSetTaskEnabled } from '../hooks/queries'
import { useOperator } from '../hooks/useOperator'
import { ChannelBadge, ConnectionError, EmptyState, PageHeader, RowsSkeleton, StatusTag } from '../components/common'
import TaskDialog from '../components/TaskDialog'
import { useSnack } from '../components/Snack'
import type { ScheduledTask, TaskState, TodayStatus } from '../api/types'
import {
  durationLabel, repeatLabel, scheduleProblem, taskStateLabel, timeRange, todayStatusLabel, wallTime, weekdaysSpoken, whenLabel,
} from '../api/schedule'
import { toneColor, toneTint, type Tone } from '../theme'
import { plural, useT, type T } from '../i18n'

const stateTone: Record<TaskState, Tone> = { running: 'critical', scheduled: 'good', paused: 'neutral', done: 'neutral' }
const todayTone: Record<TodayStatus, Tone> = { scheduled: 'neutral', running: 'critical', recorded: 'good', skipped: 'warning' }

/** Lo que dice la derecha de cada tarea: cuándo le toca, o hasta cuándo graba. */
function nextLine(t: T, task: ScheduledTask, now: string): string {
  if (task.state === 'running') return t('sched.runningUntil', { time: wallTime(task.runningUntil) })
  if (task.state === 'paused') return t('sched.pausedLine')
  if (task.state === 'done') return task.lastRun ? t('sched.doneRecorded', { when: whenLabel(task.lastRun, now) }) : t('sched.donePast')
  return t('sched.next', { when: whenLabel(task.nextRun, now) })
}

export default function SchedulePage() {
  const { t } = useT()
  const [channel, setChannel] = useState('')
  const [operator, setOperator] = useOperator()
  const channels = useChannels()
  const schedule = useSchedule(channel)
  const firstLoad = useFirstLoad(schedule)
  const setEnabled = useSetTaskEnabled()
  const remove = useDeleteTask()
  const { notify } = useSnack()

  const [editing, setEditing] = useState<{ task: ScheduledTask | null; channelId?: string } | null>(null)
  const [deleting, setDeleting] = useState<ScheduledTask | null>(null)

  const channelList = useMemo(() => (channels.data ?? []).slice().sort((a, b) => a.key.localeCompare(b.key)), [channels.data])
  const jobs = schedule.data?.jobs ?? []
  const now = schedule.data?.now ?? ''
  const today = now.slice(0, 10)

  // Una sección por canal (también los que aún no tienen tareas, para poder añadirles la primera).
  const sections = useMemo(() => {
    const shown = channel ? channelList.filter(c => c.channelId === channel) : channelList
    const known = new Set(shown.map(c => c.channelId))
    const orphan = jobs.filter(j => !known.has(j.channelId) && (!channel || j.channelId === channel))
    return [
      ...shown.map(c => ({ id: c.channelId, key: c.key, name: c.inputName, tasks: jobs.filter(j => j.channelId === c.channelId) })),
      ...(orphan.length ? [{ id: 'orphan', key: '?', name: null, tasks: orphan }] : []),
    ]
  }, [channel, channelList, jobs])

  const todays = useMemo(
    () => jobs.filter(j => j.today).slice().sort((a, b) => a.today!.start.localeCompare(b.today!.start)),
    [jobs],
  )

  const running = jobs.filter(j => j.state === 'running').length
  const subtitle = !schedule.data
    ? t('sched.subtitleIdle')
    : jobs.length === 0
      ? t('sched.subtitleNone')
      : `${plural(t, jobs.length, 'unit.task', 'unit.tasks')} · ${running === 0 ? t('sched.noneRunning') : t('sched.nRunning', { n: running })}`

  // El navegador en otra zona horaria que el Record: las horas que se ven y se escriben son SIEMPRE las del Record.
  const otherZone = schedule.data !== undefined && schedule.data.utcOffsetMinutes !== -new Date().getTimezoneOffset()

  const toggle = (task: ScheduledTask) => setEnabled.mutate({ id: task.id, enabled: !task.enabled, operator: operator.trim() || null }, {
    onSuccess: () => notify(t(task.enabled ? 'sched.pausedMsg' : 'sched.resumedMsg', { title: task.title }), 'success'),
    onError: e => notify(t(task.enabled ? 'sched.pauseFailed' : 'sched.resumeFailed', { title: task.title, error: scheduleProblem(e).message }), 'error'),
  })

  const confirmDelete = () => {
    const task = deleting
    if (!task) return
    setDeleting(null)
    remove.mutate({ id: task.id, operator: operator.trim() || null }, {
      onSuccess: () => notify(t('sched.deletedMsg', { title: task.title }), 'success'),
      onError: e => notify(t('sched.deleteFailed', { title: task.title, error: scheduleProblem(e).message }), 'error'),
    })
  }

  return (
    <Box>
      <PageHeader title={t('nav.schedule')} subtitle={subtitle}>
        <Select size="small" displayEmpty value={channel} onChange={e => setChannel(e.target.value)} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': t('filter.channel') }}>
          <MenuItem value="">{t('filter.allChannels')}</MenuItem>
          {channelList.map(c => <MenuItem key={c.channelId} value={c.channelId}>{t('unit.channelKey', { key: c.key })}{c.inputName ? ` · ${c.inputName}` : ''}</MenuItem>)}
        </Select>
        <Tooltip title={t('operator.tooltipSchedule')}>
          <TextField
            size="small"
            placeholder={t('operator.placeholder')}
            value={operator}
            onChange={e => setOperator(e.target.value)}
            sx={{ width: 190 }}
            slotProps={{
              htmlInput: { 'aria-label': t('operator.aria'), maxLength: 60 },
              input: { startAdornment: <InputAdornment position="start"><PersonOutlineRoundedIcon fontSize="small" /></InputAdornment> },
            }}
          />
        </Tooltip>
        <Button
          variant="contained"
          startIcon={<AddRoundedIcon />}
          disabled={channelList.length === 0}
          onClick={() => setEditing({ task: null, channelId: channel || undefined })}
        >
          {t('sched.newTask')}
        </Button>
      </PageHeader>

      {firstLoad.loading ? (
        <Card><RowsSkeleton rows={5} /></Card>
      ) : !schedule.data ? (
        <ConnectionError error={firstLoad.error} onRetry={() => void schedule.refetch()} />
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2.5 }}>
          {otherZone && (
            <Box role="note" sx={th => ({ display: 'flex', alignItems: 'center', gap: 1.25, px: 2, py: 1.25, borderRadius: 3, bgcolor: toneTint(th, 'accent') })}>
              <InfoOutlinedIcon sx={th => ({ fontSize: 18, color: toneColor(th, 'accent') })} />
              <Typography variant="body2">{t('sched.otherZone', { time: wallTime(now) })}</Typography>
            </Box>
          )}

          {/* HOY: lo que le toca grabar solo a cada canal, y cómo va */}
          {jobs.length > 0 && (
            <Card sx={{ p: 2.5 }}>
              <Typography variant="overline" color="text.secondary" component="h2" sx={{ display: 'block', mb: todays.length ? 1 : 0.5 }}>{t('sched.today')}</Typography>
              {todays.length === 0 ? (
                <Typography variant="body2" color="text.secondary">{t('sched.todayNone')}</Typography>
              ) : (
                <Box component="ul" sx={{ listStyle: 'none', m: 0, p: 0, display: 'flex', flexDirection: 'column' }}>
                  {todays.map((task, i) => (
                    <Box
                      component="li"
                      key={task.id}
                      sx={{ display: 'flex', alignItems: 'center', gap: 1.5, py: 1, borderTop: i ? '1px solid' : 'none', borderColor: 'divider' }}
                    >
                      <Typography sx={{ width: 108, flexShrink: 0, fontSize: 13.5, fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>
                        {wallTime(task.today!.start)} – {wallTime(task.today!.end)}
                      </Typography>
                      <ChannelBadge letter={task.channelKey ?? '?'} size={24} />
                      <Typography variant="body2" noWrap sx={{ flexGrow: 1, minWidth: 0 }}>{task.title}</Typography>
                      <StatusTag tone={todayTone[task.today!.status]} pulsing={task.today!.status === 'running'} label={todayStatusLabel(task.today!.status)} />
                    </Box>
                  ))}
                </Box>
              )}
            </Card>
          )}

          {jobs.length === 0 && (
            <Card>
              <EmptyState
                icon={<EventRepeatRoundedIcon />}
                title={t('sched.emptyTitle')}
                description={t('sched.emptyBody')}
                action={channelList.length > 0
                  ? <Button variant="contained" startIcon={<AddRoundedIcon />} onClick={() => setEditing({ task: null, channelId: channel || undefined })}>{t('sched.newTask')}</Button>
                  : undefined}
              />
            </Card>
          )}

          {jobs.length > 0 && sections.map(s => (
            <Card key={s.id} component="section" aria-label={t('sched.sectionAria', { key: s.key })}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, px: 2.5, py: 2 }}>
                <ChannelBadge letter={s.key} size={32} />
                <Box sx={{ minWidth: 0, flexGrow: 1 }}>
                  <Typography variant="h3" noWrap>{s.id === 'orphan' ? t('sched.orphanChannel') : t('unit.channelKey', { key: s.key })}</Typography>
                  <Typography variant="caption" color="text.secondary" noWrap component="div">
                    {s.name ? `${s.name} · ` : ''}{s.tasks.length === 0 ? t('sched.noTasks') : plural(t, s.tasks.length, 'unit.taskShort', 'unit.tasksShort')}
                  </Typography>
                </Box>
                {s.id !== 'orphan' && (
                  <Button size="small" startIcon={<AddRoundedIcon />} onClick={() => setEditing({ task: null, channelId: s.id })}>{t('action.add')}</Button>
                )}
              </Box>

              {s.tasks.map(task => (
                <Box key={task.id}>
                  <Divider />
                  <Box sx={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: 2, rowGap: 1, px: 2.5, py: 1.75, opacity: task.state === 'paused' || task.state === 'done' ? 0.72 : 1 }}>
                    <Box sx={{ minWidth: 0, flex: '1 1 260px' }}>
                      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
                        <Typography sx={{ fontSize: 14.5, fontWeight: 600 }} noWrap>{task.title}</Typography>
                        <StatusTag tone={stateTone[task.state]} pulsing={task.state === 'running'} label={taskStateLabel(task.state)} />
                      </Box>
                      <Typography
                        variant="caption"
                        color="text.secondary"
                        component="div"
                        sx={{ mt: 0.25, fontVariantNumeric: 'tabular-nums' }}
                        title={task.recurrence === 'Weekly' ? weekdaysSpoken(task.weekdays) : undefined}
                      >
                        {repeatLabel(task)} · {timeRange(task)} · {durationLabel(task.durationSeconds)}
                        {task.segmentMinutes ? ` · ${t('sched.filesOf', { n: task.segmentMinutes })}` : ''}
                      </Typography>
                    </Box>

                    <Typography variant="body2" color={task.state === 'running' ? 'text.primary' : 'text.secondary'} sx={{ flex: '0 1 auto', fontVariantNumeric: 'tabular-nums' }}>
                      {nextLine(t, task, now)}
                    </Typography>

                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.25, ml: 'auto' }}>
                      <Tooltip title={t(task.state === 'done' ? 'sched.tipDone' : task.enabled ? 'sched.tipPause' : 'sched.tipResume')}>
                        <span>
                          <Switch
                            size="small"
                            checked={task.enabled}
                            disabled={setEnabled.isPending || task.state === 'done'}
                            onChange={() => toggle(task)}
                            slotProps={{ input: { 'aria-label': t(task.enabled ? 'sched.pauseAria' : 'sched.resumeAria', { title: task.title }) } }}
                          />
                        </span>
                      </Tooltip>
                      <Tooltip title={t('action.edit')}>
                        <IconButton size="small" onClick={() => setEditing({ task })} aria-label={t('sched.editAria', { title: task.title })}><EditOutlinedIcon fontSize="small" /></IconButton>
                      </Tooltip>
                      <Tooltip title={t('action.delete')}>
                        <IconButton size="small" onClick={() => setDeleting(task)} aria-label={t('sched.deleteAria', { title: task.title })}><DeleteOutlineRoundedIcon fontSize="small" /></IconButton>
                      </Tooltip>
                    </Box>
                  </Box>
                </Box>
              ))}
            </Card>
          ))}
        </Box>
      )}

      <TaskDialog
        open={editing !== null}
        onClose={() => setEditing(null)}
        task={editing?.task ?? null}
        defaultChannelId={editing?.channelId}
        channels={channelList}
        today={today || new Date().toISOString().slice(0, 10)}
        operator={operator}
      />

      <Dialog open={deleting !== null} onClose={() => setDeleting(null)} maxWidth="xs" fullWidth>
        <DialogTitle>{t('sched.deleteTitle', { title: deleting?.title ?? '' })}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('sched.deleteBody', { key: deleting?.channelKey ?? '' })}</DialogContentText>
          {deleting?.state === 'running' && (
            <DialogContentText sx={{ mt: 1.5 }}>{t('sched.deleteRunning', { time: wallTime(deleting.runningUntil) })}</DialogContentText>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setDeleting(null)}>{t('sched.keepTask')}</Button>
          <Button variant="contained" color="error" onClick={confirmDelete} startIcon={<DeleteOutlineRoundedIcon />}>{t('sched.deleteTask')}</Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}
