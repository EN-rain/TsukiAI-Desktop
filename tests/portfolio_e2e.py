from pathlib import Path

from playwright.sync_api import expect, sync_playwright


PORTFOLIO = Path(r"D:\New folder (2)\TsukiAI 1.0\portfolio")


def test_portfolio_interactions() -> None:
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
        page.goto((PORTFOLIO / "index.html").as_uri())
        page.wait_for_load_state("load")
        page.evaluate("document.fonts.ready")
        expect(page.locator("#starfield")).to_be_visible()
        assert page.locator("#starfield").evaluate("canvas => canvas.width > 0 && canvas.height > 0")
        page.screenshot(
            path=r"C:\Users\LENOVO\AppData\Local\Temp\tsuki-portfolio-desktop.png",
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

        page.locator('.stack-trigger[data-stack="brain"]').click()
        expect(page.locator("#stack-title")).to_have_text("Brain and memory")

        page.locator("#about").scroll_into_view_if_needed()
        expect(page.locator('.nav a[href="#about"]')).to_have_attribute("aria-current", "true")

        mobile = context.new_page()
        mobile.set_viewport_size({"width": 390, "height": 844})
        mobile.goto((PORTFOLIO / "index.html").as_uri())
        mobile.wait_for_load_state("load")
        assert mobile.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
        mobile.screenshot(
            path=r"C:\Users\LENOVO\AppData\Local\Temp\tsuki-portfolio-mobile.png",
            full_page=True,
        )

        assert not page_errors, page_errors
        assert not console_errors, console_errors

        context.close()
        browser.close()


if __name__ == "__main__":
    test_portfolio_interactions()
