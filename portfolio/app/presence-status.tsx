"use client";

import { useEffect, useState } from "react";

export default function PresenceStatus() {
  const [online, setOnline] = useState(true);

  useEffect(() => {
    const syncConnection = () => setOnline(window.navigator.onLine);
    syncConnection();
    window.addEventListener("online", syncConnection);
    window.addEventListener("offline", syncConnection);
    return () => {
      window.removeEventListener("online", syncConnection);
      window.removeEventListener("offline", syncConnection);
    };
  }, []);

  return (
    <div className="presence" aria-live="polite">
      <span className={`presence-dot${online ? "" : " is-offline"}`} aria-hidden="true" />
      <span className="presence-copy">
        <strong id="presence-heading">{online ? "Online 24/7 on Discord" : "Browser connection paused"}</strong>
        <span id="presence-detail">{online ? "Browser connection is open" : "Reconnect to keep exploring"}</span>
      </span>
    </div>
  );
}
