import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Dev proxy: the SPA talks to the API same-origin; in production Caddy routes
// /api, /auth, /hubs to the api container.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5000",
      "/auth": "http://localhost:5000",
      "/hubs": {
        target: "http://localhost:5000",
        ws: true,
      },
    },
  },
});
