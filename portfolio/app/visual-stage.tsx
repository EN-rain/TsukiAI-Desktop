"use client";

import { useEffect, useRef } from "react";

type VisualApi = {
  mount: (node: HTMLElement) => boolean;
  clear: () => void;
  setStatus: (status: string) => void;
};

declare global {
  interface Window {
    TsukiVisual?: VisualApi;
  }
}

type VisualStageProps = {
  signal: string;
};

const FALLBACK_STATUS = "fallback scene / 3D slot ready";

export default function VisualStage({ signal }: VisualStageProps) {
  const frameRef = useRef<HTMLElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const mountRef = useRef<HTMLDivElement>(null);
  const stageStatusRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    const frame = frameRef.current;
    const stage = stageRef.current;
    const canvas = canvasRef.current;

    if (!frame || !stage || !canvas) return;
    const context = canvas.getContext("2d");
    if (!context) return;
    const frameElement: HTMLElement = frame;
    const canvasElement: HTMLCanvasElement = canvas;
    const drawingContext: CanvasRenderingContext2D = context;

    const reduceMotionQuery = window.matchMedia("(prefers-reduced-motion: reduce)");
    let width = 0;
    let height = 0;
    let pixelRatio = 1;
    let animationFrame = 0;
    let pointerX = 0;
    let pointerY = 0;
    let stars: Array<{
      x: number;
      y: number;
      radius: number;
      alpha: number;
      phase: number;
    }> = [];

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
      const bounds = canvasElement.getBoundingClientRect();
      width = bounds.width;
      height = bounds.height;
      pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
      canvasElement.width = Math.floor(width * pixelRatio);
      canvasElement.height = Math.floor(height * pixelRatio);
      drawingContext.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
      createStars();
      drawStars();
    }

    function drawStars(time = 0) {
      drawingContext.clearRect(0, 0, width, height);
      stars.forEach((star) => {
        const drift = reduceMotionQuery.matches
          ? 0
          : Math.sin(time * 0.00035 + star.phase) * 0.7;
        const x = star.x * width + pointerX * 12 * star.alpha;
        const y = star.y * height + pointerY * 12 * star.alpha + drift;
        const glow = reduceMotionQuery.matches
          ? star.alpha
          : star.alpha * (0.78 + Math.sin(time * 0.001 + star.phase) * 0.22);
        drawingContext.beginPath();
        drawingContext.fillStyle = `rgba(240, 217, 168, ${Math.max(0.08, glow)})`;
        drawingContext.arc(x, y, star.radius, 0, Math.PI * 2);
        drawingContext.fill();
      });
    }

    function animate(time: number) {
      drawStars(time);
      if (!reduceMotionQuery.matches) animationFrame = window.requestAnimationFrame(animate);
    }

    function handlePointerMove(event: PointerEvent) {
      const bounds = frameElement.getBoundingClientRect();
      pointerX = (event.clientX - bounds.left) / bounds.width - 0.5;
      pointerY = (event.clientY - bounds.top) / bounds.height - 0.5;
      frameElement.style.setProperty("--pointer-x", `${pointerX * 3}deg`);
      frameElement.style.setProperty("--pointer-y", `${pointerY * -3}deg`);
    }

    function resetPointer() {
      pointerX = 0;
      pointerY = 0;
      frameElement.style.setProperty("--pointer-x", "0deg");
      frameElement.style.setProperty("--pointer-y", "0deg");
    }

    function restartAnimation() {
      window.cancelAnimationFrame(animationFrame);
      drawStars();
      if (!reduceMotionQuery.matches) animationFrame = window.requestAnimationFrame(animate);
    }

    resizeCanvas();
    restartAnimation();
    window.addEventListener("resize", resizeCanvas);
    frameElement.addEventListener("pointermove", handlePointerMove, { passive: true });
    frameElement.addEventListener("pointerleave", resetPointer, { passive: true });
    reduceMotionQuery.addEventListener("change", restartAnimation);

    return () => {
      window.cancelAnimationFrame(animationFrame);
      window.removeEventListener("resize", resizeCanvas);
      frameElement.removeEventListener("pointermove", handlePointerMove);
      frameElement.removeEventListener("pointerleave", resetPointer);
      reduceMotionQuery.removeEventListener("change", restartAnimation);
    };
  }, []);

  useEffect(() => {
    const stage = stageRef.current;
    const mount = mountRef.current;
    const status = stageStatusRef.current;

    if (!stage || !mount || !status) return;

    const api: VisualApi = {
      mount(node) {
        if (!(node instanceof HTMLElement)) return false;
        mount.replaceChildren(node);
        stage.dataset.mode = "model";
        status.textContent = "3D scene / connected";
        return true;
      },
      clear() {
        mount.replaceChildren();
        stage.dataset.mode = "fallback";
        status.textContent = FALLBACK_STATUS;
      },
      setStatus(nextStatus) {
        status.textContent = nextStatus;
      },
    };

    window.TsukiVisual = api;

    return () => {
      if (window.TsukiVisual === api) delete window.TsukiVisual;
    };
  }, []);

  return (
    <figure className="hero-visual" ref={frameRef} aria-labelledby="visual-caption">
      <div
        className="visual-stage"
        id="visual-stage"
        ref={stageRef}
        data-mode="fallback"
        data-renderer="webgl"
      >
        <canvas id="starfield" ref={canvasRef} aria-hidden="true" />

        <div className="visual-fallback" aria-hidden="true">
          <div className="hero-orbit">
            <span className="orbit-ring orbit-ring-one" />
            <span className="orbit-ring orbit-ring-two" />
            <span className="orbit-satellite satellite-one" />
            <span className="orbit-satellite satellite-two" />
            <span className="moon-orb" />
          </div>
        </div>

        <div
          className="model-mount"
          id="model-mount"
          ref={mountRef}
          aria-label="Future 3D model mount"
        />

        <div className="stage-readout" aria-hidden="true">
          <span className="stage-index">SCENE 00</span>
          <span ref={stageStatusRef}>{FALLBACK_STATUS}</span>
        </div>
      </div>

      <figcaption id="visual-caption" className="visual-caption">
        <span className="signal-bar" aria-hidden="true" />
        <span id="visual-signal">{signal}</span>
      </figcaption>
    </figure>
  );
}
