import os
import subprocess
import time
import urllib.request
from pathlib import Path

from playwright.sync_api import expect, sync_playwright


PORTFOLIO = Path(r"D:\New folder (2)\TsukiAI 1.0\portfolio")
PORT = 3210
BASE_URL = f"http://127.0.0.1:{PORT}"


def start_server() -> subprocess.Popen:
    return subprocess.Popen(
        ["npm.cmd", "run", "start", "--", "--hostname", "127.0.0.1", "--port", str(PORT)],
        cwd=PORTFOLIO,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
    )


def wait_for_server(server: subprocess.Popen) -> None:
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        if server.poll() is not None:
            raise RuntimeError(f"Next.js server exited with code {server.returncode}")
        try:
            with urllib.request.urlopen(BASE_URL, timeout=2) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            time.sleep(0.5)
    raise TimeoutError(f"Next.js server did not start at {BASE_URL}")


def stop_server(server: subprocess.Popen) -> None:
    if server.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(server.pid), "/T", "/F"],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    else:
        server.terminate()
        server.wait(timeout=10)


def test_portfolio_interactions() -> None:
    server = start_server()
    try:
        wait_for_server(server)
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(
                headless=True,
                executable_path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
            )
            context = browser.new_context(viewport={"width": 1280, "height": 900})
            page = context.new_page()
            page_errors = []
            console_errors = []
            page.on("pageerror", lambda error: page_errors.append(str(error)))
            page.on(
                "console",
                lambda message: console_errors.append(message.text) if message.type == "error" else None,
            )
            page.goto(BASE_URL, wait_until="networkidle")
            page.wait_for_timeout(150)

            expect(page.locator("#visual-stage")).to_have_attribute("data-renderer", "webgl")
            expect(page.locator("#model-mount")).to_be_attached()
            assert page.locator("#starfield").evaluate("canvas => canvas.width > 0 && canvas.height > 0")
            assert page.evaluate("typeof window.TsukiVisual?.mount === 'function'")
            assert page.evaluate(
                """() => {
                    const probe = document.createElement('div');
                    probe.className = 'test-3d-node';
                    return window.TsukiVisual?.mount(probe) ?? false;
                }"""
            )
            expect(page.locator("#visual-stage")).to_have_attribute("data-mode", "model")
            expect(page.locator("#model-mount .test-3d-node")).to_be_visible()
            page.evaluate("window.TsukiVisual?.clear()")
            expect(page.locator("#visual-stage")).to_have_attribute("data-mode", "fallback")

            page.screenshot(
                path=r"C:\Users\LENOVO\AppData\Local\Temp\tsuki-portfolio-next-desktop.png",
                full_page=True,
            )

            theme_toggle = page.locator("#theme-toggle")
            expect(theme_toggle).to_be_visible()
            initial_theme = page.locator("html").get_attribute("data-theme")
            theme_toggle.click()
            expected_theme = "light" if initial_theme == "dark" else "dark"
            expect(page.locator("html")).to_have_attribute("data-theme", expected_theme)
            expect(theme_toggle).to_have_attribute("aria-pressed", str(expected_theme == "light").lower())

            page.get_by_role("button", name="She thinks").click()
            expect(page.locator("#pipeline-detail")).to_contain_text("semantic search")

            page.get_by_role("button", name="Run the sequence").click()
            expect(page.locator("#sequence-status")).to_have_text("Sequence complete")
            expect(page.locator("#run-sequence")).to_be_enabled()

            page.locator('.stack-trigger[data-stack="brain"]').click()
            expect(page.locator("#stack-title")).to_have_text("Brain and memory")

            page.locator("#about").scroll_into_view_if_needed()
            expect(page.locator('.nav a[href="#about"]')).to_have_attribute("aria-current", "true")

            mobile = context.new_page()
            mobile.set_viewport_size({"width": 390, "height": 844})
            mobile.goto(BASE_URL, wait_until="networkidle")
            mobile.wait_for_timeout(150)
            assert mobile.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
            mobile.screenshot(
                path=r"C:\Users\LENOVO\AppData\Local\Temp\tsuki-portfolio-next-mobile.png",
                full_page=True,
            )

            assert not page_errors, page_errors
            assert not console_errors, console_errors

            context.close()
            browser.close()
    finally:
        stop_server(server)


if __name__ == "__main__":
    test_portfolio_interactions()
