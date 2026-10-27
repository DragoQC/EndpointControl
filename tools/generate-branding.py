"""Render EndpointControl's canonical SVG into browser, MSI and native resources."""

import asyncio
import base64
import io
from pathlib import Path

from PIL import Image
from playwright.async_api import async_playwright


async def main():
    root = Path(__file__).resolve().parents[1]
    web = root / "src/MeshAll.Server/wwwroot"
    svg = (web / "favicon.svg").read_text(encoding="utf-8")
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch()
        page = await browser.new_page()
        png = await page.evaluate(
            """async svg => {
            const image = new Image();
            image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
            await image.decode();
            const canvas = document.createElement('canvas');
            canvas.width = canvas.height = 256;
            canvas.getContext('2d').drawImage(image, 0, 0, 256, 256);
            return canvas.toDataURL('image/png').split(',')[1];
        }""",
            svg,
        )
        await browser.close()

    image = Image.open(io.BytesIO(base64.b64decode(png))).convert("RGBA")
    image.save(web / "logo.png")
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    icon = io.BytesIO()
    image.save(icon, format="ICO", sizes=sizes)
    (web / "favicon.ico").write_bytes(icon.getvalue())
    branding = root / "components/branding"
    branding.mkdir(exist_ok=True)
    (branding / "EndpointControl.ico").write_bytes(icon.getvalue())
    for folder in ["meshservice", "meshconsole"]:
        (root / "components/agent-native" / folder / "MeshService.ico").write_bytes(
            icon.getvalue()
        )
    bitmap = Image.new("RGB", (200, 200), "white")
    resized = image.resize((176, 176), Image.Resampling.LANCZOS)
    bitmap.paste(resized, (12, 12), resized)
    bitmap.save(root / "components/agent-native/meshservice/mc.bmp")

    consent = io.BytesIO()
    image.resize((48, 48), Image.Resampling.LANCZOS).save(consent, format="PNG")
    module = root / "components/agent-core/modules_meshcore/meshall-consent-logo.js"
    module.write_text(
        "/* EndpointControl logo, rendered from wwwroot/favicon.svg. MIT. */\n"
        "module.exports =\n    '" + base64.b64encode(consent.getvalue()).decode() + "';\n",
        encoding="utf-8",
    )
    # Keep the retained UTF-16 resource source intact; native builds compile this branded copy.
    source = root / "components/agent-native/meshservice/MeshService.rc"
    text = source.read_text(encoding="utf-16")
    text = text.replace("MeshCentral Background Service Agent", "EndpointControl Agent")
    text = text.replace("MeshCentral Agent", "EndpointControl Agent").replace(
        "MeshAll Agent", "EndpointControl Agent"
    )
    text = "\n".join(line.rstrip() for line in text.splitlines()).rstrip() + "\n"
    (source.parent / "EndpointControl.rc").write_text(text, encoding="utf-8-sig")
    print(
        "Generated the shared EndpointControl web, consent, MSI and Windows native artwork."
    )


if __name__ == "__main__":
    asyncio.run(main())
