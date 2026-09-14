"use client";

import { useEffect, useState } from "react";

type Theme = "dark" | "light";

export default function SiteHeader() {
  const [theme, setTheme] = useState<Theme>("dark");
  const [themeReady, setThemeReady] = useState(false);
  const [activeSection, setActiveSection] = useState("home");

  useEffect(() => {
    try {
      const storedTheme = window.localStorage.getItem("tsuki-theme");
      if (storedTheme === "light" || storedTheme === "dark") setTheme(storedTheme);
    } catch {
      // The visual theme still works if storage is unavailable.
    } finally {
      setThemeReady(true);
    }
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    if (!themeReady) return;
    try {
      window.localStorage.setItem("tsuki-theme", theme);
    } catch {
      // The visual theme still works if storage is unavailable.
    }
  }, [theme, themeReady]);

  useEffect(() => {
    const sections = [...document.querySelectorAll<HTMLElement>("main [data-section]")];
    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((entry) => entry.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
        if (visible[0]) setActiveSection(visible[0].target.id);
      },
      { rootMargin: "-20% 0px -62% 0px", threshold: [0, 0.2, 0.5] },
    );

    sections.forEach((section) => observer.observe(section));
    return () => observer.disconnect();
  }, []);

  return (
    <header className="nav" data-nav>
      <div className="nav-inner">
        <a className="brand" href="#home" aria-label="Tsuki home">
          <span className="brand-mark" aria-hidden="true" />
          <span>Tsuki</span>
        </a>

        <nav aria-label="Sections">
          <a href="#about" aria-current={activeSection === "about" ? "true" : undefined}>About</a>
          <a href="#how" aria-current={activeSection === "how" ? "true" : undefined}>How she works</a>
          <a href="#stack" aria-current={activeSection === "stack" ? "true" : undefined}>Stack</a>
        </nav>

        <button
          className="theme-toggle"
          id="theme-toggle"
          type="button"
          aria-label={theme === "light" ? "Switch to dark mode" : "Switch to light mode"}
          aria-pressed={theme === "light"}
          onClick={() => setTheme(theme === "light" ? "dark" : "light")}
        >
          <span className="theme-icon" aria-hidden="true" />
          <span id="theme-label">{theme === "light" ? "Dark" : "Light"}</span>
        </button>
      </div>
      <div className="scroll-progress" aria-hidden="true">
        <span />
      </div>
    </header>
  );
}
