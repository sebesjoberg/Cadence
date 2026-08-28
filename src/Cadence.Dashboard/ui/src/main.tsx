// Placeholder entry point: enough for Vite to emit a bundle. Task 9 replaces it with the
// router, the query client and the shell layout.
import { createRoot } from 'react-dom/client'

const root = document.getElementById('root')

if (root) {
  createRoot(root).render(<div>Cadence</div>)
}
