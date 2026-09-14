# Tsuki portfolio

This is a Next.js App Router portfolio with a server-rendered content shell and small client-side islands. The page structure is rendered by `app/page.tsx`; browser-only behavior is isolated in `app/site-header.tsx`, `app/presence-status.tsx`, `app/voice-loop.tsx`, `app/stack-inspector.tsx`, and `app/visual-stage.tsx`.

```bash
npm install
npm run dev
```

## Future 3D/WebGL scene

The hero visual already exposes a client-only mount contract. When the model is ready, create its canvas in a client component and mount it after the component has initialized:

```ts
const canvas = document.createElement("canvas");
window.TsukiVisual?.mount(canvas);
```

The fallback moon scene fades back when a model is mounted. Call `window.TsukiVisual?.clear()` to restore it. Keep a heavy 3D library behind a client-only dynamic import so it does not enter the initial server-rendered bundle.
