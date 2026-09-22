import { Navigate, Route, Routes } from 'react-router-dom'
import Layout from './components/Layout'
import ChannelsPage from './pages/ChannelsPage'
import RecordingsPage from './pages/RecordingsPage'
import SchedulePage from './pages/SchedulePage'
import EventsPage from './pages/EventsPage'
import StoragePage from './pages/StoragePage'

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Navigate to="/canales" replace />} />
        <Route path="/canales" element={<ChannelsPage />} />
        <Route path="/programacion" element={<SchedulePage />} />
        <Route path="/grabaciones" element={<RecordingsPage />} />
        <Route path="/actividad" element={<EventsPage />} />
        <Route path="/almacenamiento" element={<StoragePage />} />
        <Route path="*" element={<Navigate to="/canales" replace />} />
      </Route>
    </Routes>
  )
}
