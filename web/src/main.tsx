import React from 'react'
import ReactDOM from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import '@fontsource-variable/inter'
import App from './App'
import { ColorModeProvider } from './components/ColorMode'
import { SnackProvider } from './components/Snack'
import { ConnectionDialogProvider } from './components/ConnectionDialog'
import { LanguageProvider } from './i18n'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: true, staleTime: 500 } },
})

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <LanguageProvider>
        <ColorModeProvider>
          <SnackProvider>
            <ConnectionDialogProvider>
              <BrowserRouter>
                <App />
              </BrowserRouter>
            </ConnectionDialogProvider>
          </SnackProvider>
        </ColorModeProvider>
      </LanguageProvider>
    </QueryClientProvider>
  </React.StrictMode>,
)
