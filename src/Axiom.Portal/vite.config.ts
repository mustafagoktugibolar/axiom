import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// Dev: proxy the API so the portal is same-origin (no CORS, token never leaves the origin).
export default defineConfig({
  plugins: [vue()],
  server: { proxy: { '/v1': 'http://localhost:5000' } },
});
