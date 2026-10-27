"""Real retained Guacamole.Client rendering through fragmented first-party framing."""

import argparse
import asyncio
import base64
import io
import secrets
from urllib.parse import parse_qs, urlparse

from PIL import Image
from playwright.async_api import async_playwright, expect
from localization import ready
from ui_regressions import DesktopAgent
from acceptance import rendered


def instruction(*parts):
    return ",".join(str(len(str(part))) + "." + str(part) for part in parts) + ";"


async def main(args):
    base = args.base_url.rstrip("/")
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch()
        context = await browser.new_context(
            ignore_https_errors=True,
            storage_state=args.auth_state,
            viewport={"width": 1440, "height": 1050},
        )
        page = await context.new_page()
        errors, sockets = [], []
        page.on("pageerror", lambda error: errors.append(str(error)))
        image = Image.new("RGB", (640, 360))
        image.putdata(
            [(x % 256, y % 256, (x + y) % 256) for y in range(360) for x in range(640)]
        )
        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        png = base64.b64encode(buffer.getvalue()).decode()
        wire = "".join(
            [
                instruction("name", "Écran A💻é"),
                instruction("size", 0, 640, 360),
                instruction("img", 1, 14, 0, "image/png", 0, 0),
                instruction("blob", 1, png),
                instruction("end", 1),
                instruction("sync", 1),
            ]
        ).encode("utf-8")

        def tunnel(socket):
            sockets.append(socket)
            started = False

            def message(data):
                nonlocal started
                if not started and isinstance(data, str) and data.startswith("{"):
                    started = True
                    # Binary fragments deliberately split multi-byte Unicode characters.
                    for start in range(0, len(wire), 7):
                        socket.send(wire[start : start + 7])

            socket.on_message(message)

        await context.route_web_socket("**/api/network/*/rdp?*", tunnel)
        name = "Guacamole-framing-" + secrets.token_hex(5)
        agent = None
        key_created = False
        try:
            await ready(page, base + "/enroll")
            await page.get_by_label("Key name", exact=True).fill(name)
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            link = page.locator(".download-links a").first
            await link.wait_for()
            token = parse_qs(urlparse(await link.get_attribute("href")).query)["token"][
                0
            ]
            key_created = True
            response = await context.request.post(
                base + "/api/agents/enroll",
                data={"token": token, "name": name, "platform": "Windows"},
            )
            assert response.ok
            identity = await response.json()
            agent = DesktopAgent(base, identity["deviceId"], identity["secret"], name)
            await agent.start()
            await page.goto(f"{base}/remote/{agent.identity}/rdp")
            await expect(
                page.get_by_role("button", name="Connect", exact=True)
            ).to_be_enabled()
            await page.get_by_label("Username", exact=True).fill("fixture")
            await page.get_by_label("Password", exact=True).fill("fixture-only")
            await page.get_by_role("button", name="Connect", exact=True).click()
            await expect(page.get_by_role("status")).to_have_text("Connected")
            await rendered(page.locator("#remote-vnc"))
            assert not errors, errors
            print(
                "PASS retained Guacamole renderer: Unicode names and fragmented UTF-8 graphical instructions"
            )
            sockets[-1].send(b"x.invalid;")
            await expect(page.get_by_role("status")).to_have_text(
                "Invalid remote protocol data."
            )
            await expect(
                page.get_by_role("button", name="Connect", exact=True)
            ).to_be_enabled()
            assert not errors, errors
            print(
                "PASS malformed remote framing closes cleanly without an unhandled browser exception"
            )
        finally:
            if agent:
                await agent.stop()
                await ready(page, base + "/devices/" + agent.identity)
                page.once("dialog", lambda dialog: dialog.accept())
                await page.get_by_role(
                    "button", name="Remove device", exact=True
                ).click()
                await page.wait_for_url(base + "/devices")
            if key_created:
                await ready(page, base + "/enroll")
                row = page.get_by_role("row").filter(
                    has=page.get_by_role("cell", name=name, exact=True)
                )
                if await row.count():
                    await row.get_by_role("button", name="Revoke", exact=True).click()
            await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--auth-state", required=True)
    asyncio.run(main(parser.parse_args()))
