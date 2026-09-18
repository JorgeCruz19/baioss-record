import React from 'react'
import ReactDOM from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import '@fontsource-variable/inter'
import App from './App'
import { ColorModeProvider } from './components/ColorMode'
import { SnackProvider } from './components/Snack'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: true, staleTime: 500 } },
})

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <ColorModeProvider>
        <SnackProvider>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </SnackProvider>
      </ColorModeProvider>
    </QueryClientProvider>
  </React.StrictMode>,
)
