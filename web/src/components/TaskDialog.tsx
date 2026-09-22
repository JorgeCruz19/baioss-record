import { useEffect, useState } from 'react'
import {
  Box, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, InputAdornment, MenuItem,
  Switch, TextField, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material'
import { DatePicker } from '@mui/x-date-pickers/DatePicker'
import { TimePicker } from '@mui/x-date-pickers/TimePicker'
import dayjs, { type Dayjs } from 'dayjs'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import type { ChannelStatus, Recurrence, ScheduledTask, TaskDraft, Weekday } from '../api/types'
import { WEEKDAYS, durationLabel, problemField, scheduleProblem, spanSeconds, weekdayInitial, weekdayName } from '../api/schedule'
import { useSaveTask } from '../hooks/queries'
import { useSnack } from './Snack'
import { toneColor, toneTint } from '../theme'
import { useT } from '../i18n'

interface Props {
  open: boolean
  onClose: () => void
  /** La tarea que se edita, o null para crear una. */
  task: ScheduledTask | null
  channels: ChannelStatus[]
  /** Canal preseleccionado al crear (el del filtro, o el de la sección desde la que se pulsó). */
  defaultChannelId?: string
  /** «Hoy» según el reloj del equipo que graba (yyyy-MM-dd): fecha por defecto y mínima de una tarea única. */
  today: string
  operator: string
}

interface Form {
  channelId: string
  title: string
  recurrence: Recurrence
  date: Dayjs | null
  start: Dayjs | null
  end: Dayjs | null
  weekdays: Weekday[]
  segmented: boolean
  segmentMinutes: string
}

// Las horas viven en los selectores como Dayjs (solo importa la hora del día); a la API van y vienen como «HH:mm:ss».
const timeOf = (time: string): Dayjs => dayjs(`2000-01-01T${time}`)
const asTime = (d: Dayjs): string => d.format('HH:mm:ss')
const isValid = (d: Dayjs | null): d is Dayjs => d !== null && d.isValid()

function initial(task: ScheduledTask | null, channelId: string, today: string): Form {
  if (!task) {
    return { channelId, title: '', recurrence: 'Once', date: dayjs(today), start: timeOf('20:00:00'), end: timeOf('21:00:00'), weekdays: [], segmented: false, segmentMinutes: '10' }
  }
  return {
    channelId: task.channelId,
    title: task.title,
    recurrence: task.recurrence,
    // Una única ya pasada no puede guardarse con su fecha vieja: al editarla se propone hoy.
    date: dayjs(task.recurrence === 'Once' && task.date >= today ? task.date : today),
    start: timeOf(task.startTime),
    end: timeOf(task.endTime),
    weekdays: task.weekdays,
    segmented: task.segmentMinutes !== null,
    segmentMinutes: String(task.segmentMinutes ?? 10),
  }
}

/** Crear o editar una tarea automática. Las reglas las aplica el Record (las mismas que su ventana de Programación). */
export default function TaskDialog({ open, onClose, task, channels, defaultChannelId, today, operator }: Props) {
  const { t } = useT()
  const { notify } = useSnack()
  const save = useSaveTask()
  const fallbackChannel = defaultChannelId || channels[0]?.channelId || ''
  const [form, setForm] = useState<Form>(() => initial(task, fallbackChannel, today))
  const [problem, setProblem] = useState<{ code: string | null; message: string } | null>(null)

  // Cada apertura parte de la tarea (o de un formulario limpio): nada de restos de la vez anterior.
  useEffect(() => {
    if (open) { setForm(initial(task, fallbackChannel, today)); setProblem(null); save.reset() }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, task])

  const set = <K extends keyof Form>(key: K, value: Form[K]) => { setForm(f => ({ ...f, [key]: value })); setProblem(null) }

  const timesOk = isValid(form.start) && isValid(form.end)
  const seconds = timesOk ? spanSeconds(asTime(form.start!), asTime(form.end!)) : 0
  const endsNextDay = timesOk && seconds > 0 && asTime(form.end!) < asTime(form.start!)
  const segmentMinutes = Number(form.segmentMinutes)
  const singleFile = form.segmented && segmentMinutes > 0 && seconds > 0 && segmentMinutes * 60 >= seconds
  const dateOk = form.recurrence !== 'Once' || isValid(form.date)

  // Lo que ya se sabe que el Record rechazará: se avisa antes de enviar, junto al campo.
  const localProblem =
    !form.channelId ? t('task.errChannel')
      : !timesOk ? t('task.errTimes')
        : seconds === 0 ? t('problem.endEqualsStart')
          : form.recurrence === 'Weekly' && form.weekdays.length === 0 ? t('problem.pickWeekday')
            : !dateOk ? t('problem.pickDate')
              : form.segmented && !(segmentMinutes > 0) ? t('problem.segmentMinutes')
                : null

  const field = problem?.code ? problemField[problem.code] : undefined

  const submit = () => {
    if (localProblem) { setProblem({ code: null, message: localProblem }); return }
    const draft: TaskDraft = {
      channelId: form.channelId,
      title: form.title.trim(),
      recurrence: form.recurrence,
      date: form.recurrence === 'Once' ? form.date!.format('YYYY-MM-DD') : null,
      startTime: asTime(form.start!),
      endTime: asTime(form.end!),
      weekdays: form.recurrence === 'Weekly' ? form.weekdays : [],
      segmentMinutes: form.segmented ? segmentMinutes : null,
      operator: operator.trim() || null,
    }
    save.mutate({ draft, id: task?.id }, {
      onSuccess: r => {
        notify(task ? t('task.savedUpdated', { title: r.job.title }) : t('task.savedNew', { title: r.job.title, key: r.job.channelKey ?? '' }), 'success')
        onClose()
      },
      onError: e => setProblem(scheduleProblem(e)),
    })
  }

  const durationHint = seconds > 0
    ? `${t('task.lasts', { duration: durationLabel(seconds) })}${endsNextDay ? ` · ${t('task.endsNextDay')}` : ''}. ${t('task.recordClock')}`
    : t('task.recordClock')

  return (
    <Dialog open={open} onClose={save.isPending ? undefined : onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{t(task ? 'task.editTitle' : 'task.newTitle')}</DialogTitle>
      <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2, pt: '8px !important' }}>
        <TextField
          select
          size="small"
          label={t('task.channel')}
          value={form.channelId}
          error={field === 'channel'}
          onChange={e => set('channelId', e.target.value)}
        >
          {channels.map(c => <MenuItem key={c.channelId} value={c.channelId}>{t('unit.channelKey', { key: c.key })}{c.inputName ? ` · ${c.inputName}` : ''}</MenuItem>)}
        </TextField>

        <TextField
          size="small"
          label={t('task.title')}
          placeholder={t('task.titlePlaceholder')}
          value={form.title}
          error={field === 'title'}
          onChange={e => set('title', e.target.value)}
          helperText={t('task.titleHint')}
          slotProps={{ htmlInput: { maxLength: 80 }, inputLabel: { shrink: true } }}
        />

        <Box>
          <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 0.75 }}>{t('task.repeats')}</Typography>
          <ToggleButtonGroup
            exclusive
            fullWidth
            size="small"
            value={form.recurrence}
            onChange={(_, v: Recurrence | null) => { if (v) set('recurrence', v) }}
            aria-label={t('task.repeatAria')}
          >
            <ToggleButton value="Once">{t('task.once')}</ToggleButton>
            <ToggleButton value="Daily">{t('task.daily')}</ToggleButton>
            <ToggleButton value="Weekly">{t('task.weekly')}</ToggleButton>
          </ToggleButtonGroup>
        </Box>

        {form.recurrence === 'Once' && (
          <DatePicker
            label={t('task.date')}
            value={form.date}
            minDate={dayjs(today)}
            onChange={v => set('date', v)}
            slotProps={{ textField: { size: 'small', fullWidth: true, error: field === 'date' } }}
          />
        )}

        {form.recurrence === 'Weekly' && (
          <Box>
            <Typography variant="caption" color={field === 'weekdays' ? 'error' : 'text.secondary'} component="div" sx={{ mb: 0.75 }}>{t('task.days')}</Typography>
            <ToggleButtonGroup
              fullWidth
              size="small"
              value={form.weekdays}
              onChange={(_, v: Weekday[]) => set('weekdays', WEEKDAYS.filter(d => v.includes(d)))}
              aria-label={t('task.daysAria')}
            >
              {WEEKDAYS.map(d => <ToggleButton key={d} value={d} aria-label={weekdayName(d)} sx={{ fontWeight: 650 }}>{weekdayInitial(d)}</ToggleButton>)}
            </ToggleButtonGroup>
          </Box>
        )}

        <Box>
          {/* Selectores de hora de MUI X en 24 h, con segundos como en la aplicación; se puede teclear la hora en el campo. */}
          <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 1.5 }}>
            {(['start', 'end'] as const).map(which => (
              <TimePicker
                key={which}
                label={t(which === 'start' ? 'task.start' : 'task.end')}
                value={form[which]}
                ampm={false}
                views={['hours', 'minutes', 'seconds']}
                format="HH:mm:ss"
                timeSteps={{ hours: 1, minutes: 1, seconds: 1 }}
                onChange={v => set(which, v)}
                slotProps={{ textField: { size: 'small', fullWidth: true, error: field === 'time' || (form[which] !== null && !form[which]!.isValid()) } }}
              />
            ))}
          </Box>
          <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 0.75, minHeight: 18 }}>{durationHint}</Typography>
        </Box>

        <Box>
          <FormControlLabel
            control={<Switch size="small" checked={form.segmented} onChange={e => set('segmented', e.target.checked)} />}
            label={<Typography variant="body2">{t('task.segment')}</Typography>}
          />
          {form.segmented && (
            <TextField
              size="small"
              type="number"
              label={t('task.segmentEvery')}
              value={form.segmentMinutes}
              error={field === 'segment'}
              onChange={e => set('segmentMinutes', e.target.value)}
              helperText={t(singleFile ? 'task.segmentSingle' : 'task.segmentHint')}
              sx={{ mt: 1, width: 200 }}
              slotProps={{
                htmlInput: { min: 1, max: 1440 },
                input: { endAdornment: <InputAdornment position="end">{t('unit.min')}</InputAdornment> },
                inputLabel: { shrink: true },
              }}
            />
          )}
        </Box>

        {problem && (
          <Box role="alert" sx={th => ({ display: 'flex', gap: 1, px: 1.5, py: 1.25, borderRadius: 2, bgcolor: toneTint(th, 'critical') })}>
            <ErrorOutlineRoundedIcon sx={th => ({ fontSize: 18, mt: '1px', color: toneColor(th, 'critical') })} />
            <Typography variant="body2">{problem.message}</Typography>
          </Box>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={save.isPending}>{t('action.cancel')}</Button>
        <Button
          variant="contained"
          onClick={submit}
          disabled={save.isPending}
          startIcon={save.isPending ? <CircularProgress size={14} color="inherit" /> : undefined}
        >
          {t(task ? 'task.saveChanges' : 'task.schedule')}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
