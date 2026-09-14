(() => {
  "use strict";

  const root = document.documentElement;
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

  const themeToggle = document.querySelector("#theme-toggle");
  const themeLabel = document.querySelector("#theme-label");

  function readTheme() {
    try {
      return localStorage.getItem("tsuki-theme") || "dark";
    } catch {
      return "dark";
    }
  }

  function setTheme(theme) {
    const isLight = theme === "light";
    root.dataset.theme = isLight ? "light" : "dark";
    themeToggle?.setAttribute("aria-pressed", String(isLight));
    themeToggle?.setAttribute(
      "aria-label",
      isLight ? "Switch to dark mode" : "Switch to light mode",
    );
    if (themeLabel) themeLabel.textContent = isLight ? "Dark" : "Light";

    try {
      localStorage.setItem("tsuki-theme", root.dataset.theme);
    } catch {
      // Private browsing can deny storage. The visual toggle still works.
    }
  }

  setTheme(readTheme());
  themeToggle?.addEventListener("click", () => {
    setTheme(root.dataset.theme === "light" ? "dark" : "light");
  });

  const sections = [...document.querySelectorAll("main [data-section]")];
  const sectionLinks = [...document.querySelectorAll('.nav a[href^="#"]')];

  function setActiveSection(id) {
    sectionLinks.forEach((link) => {
      const active = link.getAttribute("href") === `#${id}`;
      if (active) link.setAttribute("aria-current", "true");
      else link.removeAttribute("aria-current");
    });
  }

  setActiveSection("home");
  if ("IntersectionObserver" in window) {
    const sectionObserver = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((entry) => entry.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
        if (visible[0]) setActiveSection(visible[0].target.id);
      },
      { rootMargin: "-20% 0px -62% 0px", threshold: [0, 0.2, 0.5] },
    );
    sections.forEach((section) => sectionObserver.observe(section));
  }

  const pipeline = {
    listen: {
      kicker: "voice input",
      title: "She listens",
      copy: "Voice activity detection splits the channel audio into turns, which are transcribed by Whisper.",
      signal: "ambient / always listening",
    },
    think: {
      kicker: "context engine",
      title: "She thinks",
      copy: "A persona-tuned LLM builds the reply, with semantic search over long-term memory when the moment calls for it.",
      signal: "memory / context intact",
    },
    speak: {
      kicker: "voice output",
      title: "She speaks",
      copy: "Her reply is synthesized into a natural voice and streamed back into the voice channel.",
      signal: "voice / ready to answer",
    },
  };

  const pipelineSteps = [...document.querySelectorAll(".process-step")];
  const detailKicker = document.querySelector("#detail-kicker");
  const detailTitle = document.querySelector("#detail-title");
  const detailCopy = document.querySelector("#detail-copy");
  const visualSignal = document.querySelector("#visual-signal");
  const sequenceStatus = document.querySelector("#sequence-status");
  const runSequence = document.querySelector("#run-sequence");
  let sequenceRun = 0;

  function setPipelineStep(key) {
    const content = pipeline[key];
    if (!content) return;
    const activeIndex = ["listen", "think", "speak"].indexOf(key);

    pipelineSteps.forEach((step, index) => {
      const active = step.dataset.step === key;
      step.classList.toggle("is-active", active);
      step.classList.toggle("is-complete", index < activeIndex);
      step.querySelector(".process-trigger")?.setAttribute("aria-expanded", String(active));
    });

    if (detailKicker) detailKicker.textContent = content.kicker;
    if (detailTitle) detailTitle.textContent = content.title;
    if (detailCopy) detailCopy.textContent = content.copy;
    if (visualSignal) visualSignal.textContent = content.signal;
  }

  pipelineSteps.forEach((step) => {
    step.querySelector(".process-trigger")?.addEventListener("click", () => {
      setPipelineStep(step.dataset.step);
      if (sequenceStatus) sequenceStatus.textContent = "Ready when you are";
    });
  });

  runSequence?.addEventListener("click", () => {
    const run = ++sequenceRun;
    runSequence.disabled = true;
    runSequence.setAttribute("aria-busy", "true");
    pipelineSteps.forEach((step) => step.classList.remove("is-complete"));

    const delay = reduceMotion.matches ? 40 : 620;
    ["listen", "think", "speak"].forEach((key, index) => {
      window.setTimeout(() => {
        if (run !== sequenceRun) return;
        setPipelineStep(key);
        pipelineSteps
          .find((step) => step.dataset.step === key)
          ?.classList.toggle("is-complete", index === 2);
        if (sequenceStatus) {
          sequenceStatus.textContent = index === 2 ? "Sequence complete" : `${pipeline[key].title}...`;
        }
      }, delay * index);
    });

    window.setTimeout(() => {
      if (run !== sequenceRun) return;
      runSequence.disabled = false;
      runSequence.removeAttribute("aria-busy");
      runSequence.innerHTML = 'Run it again <span aria-hidden="true">↻</span>';
    }, delay * 2 + (reduceMotion.matches ? 80 : 160));
  });

  const stackContent = {
    bridge: {
      title: "Discord voice bridge",
      copy: "The doorway into the room. It turns live Discord audio into clean, intentional turns for Tsuki to understand.",
    },
    brain: {
      title: "Brain and memory",
      copy: "The quiet middle layer. It keeps her persona coherent and reaches for older context only when it helps.",
    },
    voice: {
      title: "Voice",
      copy: "A short path from words to presence: transcription, translation, and synthesis tuned for conversation.",
    },
    home: {
      title: "Home",
      copy: "A self-hosted room in Azure, composed with Docker and kept ready for the next late-night conversation.",
    },
  };

  const stackTitle = document.querySelector("#stack-title");
  const stackCopy = document.querySelector("#stack-copy");
  document.querySelectorAll(".stack-trigger").forEach((trigger) => {
    trigger.addEventListener("click", () => {
      const content = stackContent[trigger.dataset.stack];
      if (!content) return;
      document.querySelectorAll(".stack-trigger").forEach((item) => {
        const selected = item === trigger;
        item.classList.toggle("is-selected", selected);
        item.setAttribute("aria-expanded", String(selected));
      });
      if (stackTitle) stackTitle.textContent = content.title;
      if (stackCopy) stackCopy.textContent = content.copy;
    });
  });

  const presenceHeading = document.querySelector("#presence-heading");
  const presenceDetail = document.querySelector("#presence-detail");
  const presenceDot = document.querySelector(".presence-dot");

  function updateConnectionStatus() {
    const online = navigator.onLine;
    presenceHeading.textContent = online ? "Online 24/7 on Discord" : "Browser connection paused";
    presenceDetail.textContent = online ? "Browser connection is open" : "Reconnect to keep exploring";
    presenceDot?.classList.toggle("is-offline", !online);
  }

  updateConnectionStatus();
  window.addEventListener("online", updateConnectionStatus);
  window.addEventListener("offline", updateConnectionStatus);

  const canvas = document.querySelector("#starfield");
  const heroVisual = document.querySelector(".hero-visual");
  if (canvas && heroVisual) {
    const context = canvas.getContext("2d");
    let width = 0;
    let height = 0;
    let pixelRatio = 1;
    let stars = [];
    let pointerX = 0;
    let pointerY = 0;

    function createStars() {
      const amount = Math.max(48, Math.floor((width * height) / 10500));
      stars = Array.from({ length: amount }, (_, index) => ({
        x: ((index * 47) % 1000) / 1000,
        y: ((index * 83 + 17) % 1000) / 1000,
        radius: 0.45 + ((index * 13) % 100) / 170,
        alpha: 0.2 + ((index * 29) % 80) / 100,
        phase: index * 0.73,
      }));
    }

    function resizeCanvas() {
      const bounds = canvas.getBoundingClientRect();
      width = bounds.width;
      height = bounds.height;
      pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.floor(width * pixelRatio);
      canvas.height = Math.floor(height * pixelRatio);
      context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
      createStars();
    }

    function drawStars(time = 0) {
      context.clearRect(0, 0, width, height);
      stars.forEach((star) => {
        const drift = reduceMotion.matches ? 0 : Math.sin(time * 0.00035 + star.phase) * 0.7;
        const x = star.x * width + pointerX * 12 * star.alpha;
        const y = star.y * height + pointerY * 12 * star.alpha + drift;
        const glow = reduceMotion.matches
          ? star.alpha
          : star.alpha * (0.78 + Math.sin(time * 0.001 + star.phase) * 0.22);
        context.beginPath();
        context.fillStyle = `rgba(240, 217, 168, ${Math.max(0.08, glow)})`;
        context.arc(x, y, star.radius, 0, Math.PI * 2);
        context.fill();
      });
    }

    function animate(time) {
      drawStars(time);
      if (!reduceMotion.matches) window.requestAnimationFrame(animate);
    }

    resizeCanvas();
    drawStars();
    if (!reduceMotion.matches) window.requestAnimationFrame(animate);
    window.addEventListener("resize", resizeCanvas);

    heroVisual.addEventListener("pointermove", (event) => {
      const bounds = heroVisual.getBoundingClientRect();
      pointerX = (event.clientX - bounds.left) / bounds.width - 0.5;
      pointerY = (event.clientY - bounds.top) / bounds.height - 0.5;
      heroVisual.style.setProperty("--pointer-x", `${pointerX * 3}deg`);
      heroVisual.style.setProperty("--pointer-y", `${pointerY * -3}deg`);
    });

    heroVisual.addEventListener("pointerleave", () => {
      pointerX = 0;
      pointerY = 0;
      heroVisual.style.setProperty("--pointer-x", "0deg");
      heroVisual.style.setProperty("--pointer-y", "0deg");
    });
  }
})();
