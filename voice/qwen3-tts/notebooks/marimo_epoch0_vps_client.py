"""Marimo client for the epoch-0 Qwen3-TTS service.

This file does not contain credentials and does not start a tunnel. It connects
to the existing public TTS endpoint. The Cloudflare token field is included
only for future tunnel-hosting use; it is not needed for the current endpoint.
"""

import marimo


app = marimo.App(width="medium")


@app.cell
def _():
    import marimo as mo
    import requests

    API_URL = "https://colab-tts.tsukihime.dpdns.org"

    return API_URL, mo, requests


@app.cell
def _(mo):
    tts_key_input = mo.ui.text(
        label="TTS API key",
        kind="password",
        placeholder="Paste the key here; it will not be displayed",
        full_width=True,
    )
    cloudflare_token_input = mo.ui.text(
        label="Cloudflare tunnel token (optional)",
        kind="password",
        placeholder="Only needed if Marimo starts its own tunnel",
        full_width=True,
    )

    mo.vstack(
        [
            tts_key_input,
            cloudflare_token_input,
            mo.md(
                "The current public endpoint needs only the TTS API key. "
                "Do not put either secret into source code."
            ),
        ]
    )

    return cloudflare_token_input, tts_key_input


@app.cell
def _(API_URL, mo, requests, tts_key_input):
    TTS_API_KEY = tts_key_input.value.strip()

    if not TTS_API_KEY:
        health_output = mo.callout(
            "Enter the TTS API key above to check the service.",
            kind="warn",
        )
    else:
        health_response = requests.get(
            f"{API_URL}/health",
            headers={"X-Api-Key": TTS_API_KEY},
            timeout=30,
        )

        try:
            payload = health_response.json()
        except ValueError:
            payload = {}

        health_output = mo.callout(
            mo.md(
                f"HTTP `{health_response.status_code}`  \n"
                f"Mode: `{payload.get('mode', 'unknown')}`  \n"
                f"Profile: `{payload.get('profile', 'unknown')}`"
            ),
            kind="success" if health_response.ok else "danger",
        )

    health_output

    return TTS_API_KEY, health_output


@app.cell
def _(mo):
    text_input = mo.ui.text_area(
        label="Text",
        value="Hello, this is Tsuki speaking.",
        full_width=True,
    )
    language_input = mo.ui.dropdown(
        options=["English", "Japanese"],
        value="English",
        label="Language",
    )
    generate_button = mo.ui.run_button(label="Generate voice")

    mo.vstack([text_input, language_input, generate_button])

    return generate_button, language_input, text_input


@app.cell
def _(
    API_URL,
    TTS_API_KEY,
    generate_button,
    language_input,
    mo,
    requests,
    text_input,
):
    if not generate_button.value:
        result = mo.md("Enter text, then click **Generate voice**.")
    elif not TTS_API_KEY:
        result = mo.callout("TTS API key is missing.", kind="danger")
    else:
        tts_response = requests.post(
            f"{API_URL}/tts",
            headers={"X-Api-Key": TTS_API_KEY},
            json={
                "text": text_input.value,
                "language": language_input.value,
            },
            timeout=120,
        )

        if tts_response.ok:
            result = mo.vstack(
                [
                    mo.md(f"Generated `{len(tts_response.content):,}` audio bytes."),
                    mo.audio(tts_response.content),
                ]
            )
        else:
            result = mo.callout(
                f"TTS request failed: HTTP {tts_response.status_code}",
                kind="danger",
            )

    result

    return result,


if __name__ == "__main__":
    app.run()
