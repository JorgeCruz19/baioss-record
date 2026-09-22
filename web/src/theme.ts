import { alpha, createTheme, type Theme } from '@mui/material/styles'

export type Mode = 'light' | 'dark'

/**
 * Tinta, superficies, acento y colores de ESTADO. El claro y el oscuro están ELEGIDOS por separado (no es una
 * inversión automática): neutros cálidos, un solo acento (azul) y cuatro colores de estado reservados, que en la
 * interfaz van SIEMPRE con icono + etiqueta (nunca el color solo) y con el texto en tinta normal.
 */
const tokens = {
  light: {
    page: '#f7f7f5',
    surface: '#ffffff',
    ink: '#0b0b0b',
    ink2: '#52514e',
    muted: '#898781',
    hairline: '#e6e5df',
    accent: '#2a78d6',
    good: '#0ca30c',
    warning: '#fab219',
    serious: '#ec835a',
    critical: '#d03b3b',
  },
  dark: {
    page: '#0d0d0d',
    surface: '#1a1a19',
    ink: '#ffffff',
    ink2: '#c3c2b7',
    muted: '#898781',
    hairline: '#2c2c2a',
    accent: '#3987e5',
    good: '#0ca30c',
    warning: '#fab219',
    serious: '#ec835a',
    critical: '#d03b3b',
  },
} as const

export type Tone = 'neutral' | 'accent' | 'good' | 'warning' | 'serious' | 'critical'

/** Color de un tono de estado/acento en el tema vigente. */
export function toneColor(theme: Theme, tone: Tone): string {
  const t = tokens[theme.palette.mode]
  switch (tone) {
    case 'accent': return t.accent
    case 'good': return t.good
    case 'warning': return t.warning
    case 'serious': return t.serious
    case 'critical': return t.critical
    default: return t.muted
  }
}

/** Fondo tintado (mismo tono, muy suave) para etiquetas y pistas de medidor. */
export function toneTint(theme: Theme, tone: Tone, strength = 1): string {
  const base = theme.palette.mode === 'dark' ? 0.2 : 0.12
  return alpha(toneColor(theme, tone), Math.min(1, base * strength))
}

const fontFamily = '"Inter Variable", Inter, system-ui, -apple-system, "Segoe UI", sans-serif'

export function buildTheme(mode: Mode): Theme {
  const t = tokens[mode]
  const dark = mode === 'dark'
  const softShadow = dark ? 'none' : '0 1px 2px rgba(11, 11, 11, 0.04)'
  const floatShadow = dark
    ? '0 12px 32px rgba(0, 0, 0, 0.55)'
    : '0 12px 32px rgba(11, 11, 11, 0.10), 0 2px 6px rgba(11, 11, 11, 0.05)'

  return createTheme({
    palette: {
      mode,
      background: { default: t.page, paper: t.surface },
      text: { primary: t.ink, secondary: t.ink2, disabled: t.muted },
      divider: t.hairline,
      primary: { main: t.accent, contrastText: '#ffffff' },
      info: { main: t.accent, contrastText: '#ffffff' },
      success: { main: t.good, contrastText: '#ffffff' },
      warning: { main: t.warning, contrastText: '#0b0b0b' },
      error: { main: t.critical, contrastText: '#ffffff' },
      action: {
        hover: alpha(t.ink, dark ? 0.06 : 0.04),
        selected: alpha(t.ink, dark ? 0.1 : 0.07),
        focus: alpha(t.ink, 0.12),
        disabled: alpha(t.ink, 0.32),
        disabledBackground: alpha(t.ink, dark ? 0.1 : 0.07),
      },
    },
    shape: { borderRadius: 12 },
    typography: {
      fontFamily,
      fontSize: 14,
      h1: { fontSize: 26, fontWeight: 650, letterSpacing: '-0.02em', lineHeight: 1.2 },
      h2: { fontSize: 17, fontWeight: 650, letterSpacing: '-0.01em', lineHeight: 1.3 },
      h3: { fontSize: 15, fontWeight: 600, lineHeight: 1.35 },
      body1: { fontSize: 14, lineHeight: 1.55 },
      body2: { fontSize: 13, lineHeight: 1.5 },
      caption: { fontSize: 12, lineHeight: 1.4 },
      button: { textTransform: 'none', fontWeight: 600, fontSize: 13.5, letterSpacing: 0 },
    },
    components: {
      MuiCssBaseline: {
        styleOverrides: {
          body: {
            backgroundColor: t.page,
            WebkitFontSmoothing: 'antialiased',
            MozOsxFontSmoothing: 'grayscale',
            textRendering: 'optimizeLegibility',
          },
          '::selection': { backgroundColor: alpha(t.accent, 0.25) },
          '*': { scrollbarWidth: 'thin', scrollbarColor: `${alpha(t.ink, 0.22)} transparent` },
          '@media (prefers-reduced-motion: reduce)': {
            '*, *::before, *::after': { animationDuration: '0.001ms !important', transitionDuration: '0.001ms !important' },
          },
        },
      },
      MuiPaper: { styleOverrides: { root: { backgroundImage: 'none' } } },
      MuiCard: {
        defaultProps: { variant: 'outlined' },
        styleOverrides: { root: { borderRadius: 16, borderColor: t.hairline, boxShadow: softShadow } },
      },
      MuiButton: {
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: { borderRadius: 10, paddingInline: 16, minHeight: 36, transition: 'background-color .15s, border-color .15s, box-shadow .15s' },
          sizeSmall: { minHeight: 30, paddingInline: 10, fontSize: 13 },
          sizeLarge: { minHeight: 42, paddingInline: 20, fontSize: 14 },
          outlined: { borderColor: t.hairline, color: t.ink, '&:hover': { borderColor: alpha(t.ink, 0.3), backgroundColor: alpha(t.ink, 0.04) } },
          text: { color: t.ink2, '&:hover': { backgroundColor: alpha(t.ink, 0.05), color: t.ink } },
        },
      },
      MuiIconButton: { styleOverrides: { root: { borderRadius: 10, color: t.ink2 } } },
      MuiOutlinedInput: {
        styleOverrides: {
          root: {
            borderRadius: 10,
            backgroundColor: t.surface,
            fontSize: 14,
            // Los campos necesitan un borde algo más presente que las tarjetas para que se reconozcan como editables.
            '& .MuiOutlinedInput-notchedOutline': { borderColor: alpha(t.ink, dark ? 0.18 : 0.16), transition: 'border-color .15s' },
            '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: alpha(t.ink, 0.36) },
            '&.Mui-focused .MuiOutlinedInput-notchedOutline': { borderColor: t.accent, borderWidth: 1.5 },
          },
        },
      },
      MuiInputLabel: { styleOverrides: { root: { fontSize: 14 } } },
      MuiInputAdornment: { styleOverrides: { root: { color: t.muted, '& .MuiTypography-root': { fontSize: 13, color: t.muted } } } },
      MuiChip: { styleOverrides: { root: { borderRadius: 999, fontWeight: 550, height: 26 } } },
      MuiTooltip: {
        defaultProps: { arrow: false, enterDelay: 300 },
        styleOverrides: {
          tooltip: {
            backgroundColor: dark ? '#f1f0ec' : '#1a1a19',
            color: dark ? '#0b0b0b' : '#ffffff',
            fontSize: 12,
            fontWeight: 500,
            lineHeight: 1.45,
            borderRadius: 8,
            padding: '7px 10px',
            maxWidth: 320,
          },
        },
      },
      MuiTableCell: {
        styleOverrides: {
          root: { borderBottomColor: t.hairline, padding: '12px 16px', fontSize: 13.5 },
          head: { fontSize: 12, fontWeight: 600, color: t.ink2, backgroundColor: 'transparent', whiteSpace: 'nowrap' },
        },
      },
      MuiTableRow: { styleOverrides: { root: { '&:last-child td': { borderBottom: 0 } } } },
      MuiDialog: {
        styleOverrides: { paper: { borderRadius: 16, border: `1px solid ${t.hairline}`, boxShadow: floatShadow, backgroundImage: 'none' } },
      },
      MuiDialogTitle: { styleOverrides: { root: { fontSize: 17, fontWeight: 650, paddingBottom: 8 } } },
      MuiDialogActions: { styleOverrides: { root: { padding: '8px 20px 20px' } } },
      MuiMenu: {
        styleOverrides: {
          paper: { borderRadius: 12, border: `1px solid ${t.hairline}`, boxShadow: floatShadow, marginTop: 4, backgroundImage: 'none' },
          list: { padding: 4 },
        },
      },
      MuiMenuItem: {
        styleOverrides: { root: { borderRadius: 8, fontSize: 13.5, minHeight: 36, '&.Mui-selected': { backgroundColor: alpha(t.ink, dark ? 0.1 : 0.06) } } },
      },
      MuiListItemButton: {
        styleOverrides: {
          root: {
            borderRadius: 10,
            '&.Mui-selected': { backgroundColor: alpha(t.ink, dark ? 0.1 : 0.06), '&:hover': { backgroundColor: alpha(t.ink, dark ? 0.13 : 0.08) } },
          },
        },
      },
      MuiToggleButtonGroup: { styleOverrides: { root: { backgroundColor: alpha(t.ink, dark ? 0.08 : 0.05), borderRadius: 10, padding: 3, gap: 2 } } },
      MuiToggleButton: {
        styleOverrides: {
          root: {
            border: 0,
            borderRadius: '8px !important',
            textTransform: 'none',
            fontWeight: 600,
            fontSize: 13,
            color: t.ink2,
            padding: '4px 12px',
            '&.Mui-selected': { backgroundColor: t.surface, color: t.ink, boxShadow: dark ? 'none' : '0 1px 2px rgba(11,11,11,0.10)', '&:hover': { backgroundColor: t.surface } },
          },
        },
      },
      MuiSwitch: {
        styleOverrides: {
          root: { width: 40, height: 24, padding: 0, margin: 8 },
          switchBase: {
            padding: 0,
            margin: 3,
            transitionDuration: '200ms',
            '&.Mui-checked': {
              transform: 'translateX(16px)',
              color: '#ffffff',
              '& + .MuiSwitch-track': { backgroundColor: t.accent, opacity: 1 },
            },
          },
          thumb: { width: 18, height: 18, boxShadow: '0 1px 2px rgba(0,0,0,0.25)' },
          // Tamaño PEQUEÑO a medida. Sin esto, los estilos de MUI para size="small" (selectores anidados, más
          // específicos que estos) le ponían relleno 4 px y bolita de 16 px sobre el diseño de 24 px de alto: la bolita
          // quedaba a 7 px del borde de arriba y a 1 del de abajo, visiblemente descentrada.
          sizeSmall: {
            width: 34, height: 20, padding: 0, margin: 6,
            '& .MuiSwitch-switchBase': { padding: 0, margin: 2, '&.Mui-checked': { transform: 'translateX(14px)' } },
            '& .MuiSwitch-thumb': { width: 16, height: 16 },
            '& .MuiSwitch-track': { borderRadius: 10 },
          },
          track: { borderRadius: 12, backgroundColor: alpha(t.ink, dark ? 0.28 : 0.2), opacity: 1 },
        },
      },
      MuiSkeleton: { defaultProps: { animation: 'wave' }, styleOverrides: { root: { backgroundColor: alpha(t.ink, dark ? 0.08 : 0.06), borderRadius: 8 } } },
      MuiDivider: { styleOverrides: { root: { borderColor: t.hairline } } },
      MuiDrawer: { styleOverrides: { paper: { backgroundImage: 'none', backgroundColor: t.page, borderRight: `1px solid ${t.hairline}` } } },
    },
  })
}
