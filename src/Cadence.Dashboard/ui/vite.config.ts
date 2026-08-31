import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  // Fixed, because the bundle ships prebuilt inside a NuGet package: the consumer has no
  // build in which to bake a prefix.
  base: '/cadence/',
  plugins: [react()],
  build: { outDir: '../wwwroot', emptyOutDir: true },
  test: { environment: 'jsdom', setupFiles: ['./src/test/setup.ts'] },
})
