// Molab identity gate used by the strict Molab voice-channel route.
export function matchesMolabHealth(health) {
  return health?.status === 'ready'
    && health.mode === 'trained-base-full-icl'
    && health.checkpoint_id === 'epoch-0'
    && (health.voice_concurrency_limit === 3 ||
      (health.voice_concurrency_limit === 1 &&
        health.gpu_batch_size_limit === 3 &&
        health.gpu_batch_endpoint === '/tts-batch'));
}
