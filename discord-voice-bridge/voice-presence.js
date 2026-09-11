function getMemberValues(members) {
  return members && typeof members.values === 'function'
    ? [...members.values()]
    : Array.isArray(members)
      ? members
      : [];
}

export function countHumanVoiceMembers(members) {
  return getMemberValues(members).filter((member) => !member?.user?.bot).length;
}

export function countEligibleHumanVoiceMembers(members, manualFocusList) {
  const focusList = manualFocusList instanceof Set ? manualFocusList : new Set();

  return getMemberValues(members).filter((member) => {
    if (member?.user?.bot) return false;
    if (focusList.size === 0) return true;

    const memberId = member?.id || member?.user?.id;
    return focusList.has(memberId);
  }).length;
}

export function shouldEnableAssemblyRealtime({
  isAssemblyMode,
  hasVoiceConnection,
  humanMemberCount,
}) {
  return Boolean(isAssemblyMode && hasVoiceConnection && Number(humanMemberCount) > 0);
}
