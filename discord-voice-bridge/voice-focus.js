/**
 * Decide whether a Discord speaking event may create an audio capture.
 *
 * Realtime AssemblyAI uses one session per allowed Discord user, so it must
 * not inherit the legacy single-user focus lock or global turn gate. The
 * realtime capture itself still refuses audio while Tsuki is already playing.
 */
export function shouldAcceptVoiceStart({
  userId,
  manualFocusList,
  assemblyRealtimeRequested,
  focusedUserId,
  turnGateClosed,
}) {
  const focusList = manualFocusList instanceof Set ? manualFocusList : new Set();

  if (focusList.size > 0 && !focusList.has(userId)) {
    return false;
  }

  if (assemblyRealtimeRequested) {
    return true;
  }

  if (turnGateClosed) {
    return false;
  }

  return !focusedUserId || focusedUserId === userId;
}
