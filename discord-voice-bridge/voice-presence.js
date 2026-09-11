export function countHumanVoiceMembers(members) {
  const values = members && typeof members.values === 'function'
    ? [...members.values()]
    : Array.isArray(members)
      ? members
      : [];

  return values.filter((member) => !member?.user?.bot).length;
}

export function shouldEnableAssemblyRealtime({
  isAssemblyMode,
  hasVoiceConnection,
  humanMemberCount,
}) {
  return Boolean(isAssemblyMode && hasVoiceConnection && Number(humanMemberCount) > 0);
}
