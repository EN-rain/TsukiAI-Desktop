import SiteHeader from "./site-header";
import StackInspector from "./stack-inspector";
import VisualStage from "./visual-stage";
import VoiceLoop from "./voice-loop";
import PresenceStatus from "./presence-status";

export default function Page() {
  return (
    <div className="site-shell">
      <SiteHeader />

      <main>
        <section id="home" className="hero" data-section>
          <div className="hero-copy">
            <p className="eyebrow">Voice companion for late hours</p>
            <h1>Tsuki</h1>
            <p className="hero-line">A voice that stays in the room.</p>
            <p className="tagline">
              A night-sky companion who lives in your voice channel - listening,
              thinking, and answering out loud, around the clock.
            </p>

            <div className="hero-actions">
              <a className="button button-primary" href="#how">
                Explore her loop
                <span aria-hidden="true">&#8599;</span>
              </a>
              <a className="text-link" href="#about">
                Meet Tsuki
                <span aria-hidden="true">&#8595;</span>
              </a>
            </div>

            <PresenceStatus />
          </div>

          <VisualStage signal="ambient / always listening" />
        </section>

        <section id="about" className="section about-section" data-section>
          <div className="section-heading">
            <h2>About</h2>
            <p className="section-lede">A persistent presence for the in-between moments.</p>
          </div>

          <div className="about-grid">
            <div className="about-copy">
              <p>
                Tsuki is a self-hosted AI companion. She sits in a Discord voice channel,
                hears when you speak, understands the room's context, and replies with her
                own synthesized voice - no button presses, no wake words. Between turns she
                remembers what matters to you, so conversations pick up where they left off.
              </p>
              <p>
                She started as a desktop app for late-night chats and grew into a persistent
                presence: the same persona and memories, available from anywhere Discord is.
              </p>
            </div>

            <aside className="memory-note" aria-label="Tsuki memory note">
              <span className="memory-mark" aria-hidden="true">&#10023;</span>
              <p>She keeps the thread between conversations.</p>
              <span className="memory-caption">long-term memory / on</span>
            </aside>
          </div>
        </section>

        <section id="how" className="section process-section" data-section>
          <div className="section-heading">
            <h2>How she works</h2>
            <p className="section-lede">Choose a handoff to see how one voice turn moves through Tsuki.</p>
          </div>

          <VoiceLoop />
        </section>

        <section id="stack" className="section stack-section" data-section>
          <div className="section-heading">
            <h2>Under the hood</h2>
            <p className="section-lede">Quiet machinery, clear purpose.</p>
          </div>

          <StackInspector />
        </section>
      </main>

      <footer className="footer">
        <p>Tsuki runs herself - deployed, not hosted. <span className="whisper">zzz</span></p>
        <a href="#home">Back to the sky <span aria-hidden="true">&#8593;</span></a>
      </footer>
    </div>
  );
}
