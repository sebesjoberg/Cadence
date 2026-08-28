import '@mantine/core/styles.css'
import '@mantine/notifications/styles.css'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './app'
import { bootstrap } from './bootstrap'

const container = document.getElementById('root')

if (container === null) {
  throw new Error('The shell document carries no #root element for the dashboard to mount into.')
}

// Names the deployment in the tab, which is the whole reason the shell sends a title.
document.title = bootstrap.title

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
