"""Fullscreen survives disconnects and remains escapable, independent of transport state."""

import argparse
import asyncio
import secrets
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from playwright.async_api import async_playwright, expect
from localization import ready, select_language, text
from ui_regressions import DesktopAgent


async def fullscreen(page, enabled, language):
    name = text("Fullscreen" if enabled else "Exit fullscreen", language)
    button = page.get_by_role("button", name=name, exact=True)
    await expect(button).to_be_enabled()
    await button.click()
    await page.wait_for_function(
        "enabled => !!document.fullscreenElement === enabled", arg=enabled
    )
    await expect(page.locator("[data-remote-fullscreen]")).to_have_attribute(
        "aria-pressed", str(enabled).lower()
    )
    next_name = text("Exit fullscreen" if enabled else "Fullscreen", language)
    await expect(page.get_by_role("button", name=next_name, exact=True)).to_be_visible()


async def main(args):
    base = args.base_url.rstrip("/")
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch()
        context = await browser.new_context(
            ignore_https_errors=True,
            storage_state=args.auth_state,
            locale="en-US",
            viewport={"width": 1440, "height": 1000},
        )
        page = await context.new_page()
        errors, terminal_sockets = [], []
        page.on("pageerror", lambda error: errors.append(str(error)))

        def terminal_socket(socket):
            terminal_sockets.append(socket)
            socket.on_message(lambda message: None)
            socket.send(b"MeshAll isolated terminal fixture\r\n> ")

        await context.route_web_socket("**/api/network/*/terminal", terminal_socket)
        await context.route_web_socket("**/api/network/*/powershell", terminal_socket)
        fail_terminal_asset = True

        async def terminal_asset(route):
            nonlocal fail_terminal_asset
            if fail_terminal_asset:
                fail_terminal_asset = False
                await route.abort()
            else:
                await route.continue_()

        await page.route("**/vendor/xterm/xterm.js", terminal_asset)
        name = "Fullscreen-fixture-" + secrets.token_hex(4)
        agent = None
        language = "en"
        created_key = False
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
            created_key = True
            response = await context.request.post(
                base + "/api/agents/enroll",
                data={"token": token, "name": name, "platform": "Windows"},
            )
            assert response.ok, await response.text()
            identity = await response.json()
            agent = DesktopAgent(base, identity["deviceId"], identity["secret"], name)
            await agent.start()

            for language in ["en", "fr"]:
                await ready(page, base + "/devices/" + agent.identity)
                if language == "fr":
                    await select_language(page, "en", "fr")
                for tool in ["desktop", "terminal", "powershell"]:
                    await page.goto(f"{base}/remote/{agent.identity}/{tool}")
                    connect = page.get_by_role(
                        "button", name=text("Connect", language), exact=True
                    )
                    await expect(connect).to_be_enabled()
                    await expect(page.get_by_role("status")).to_have_text(
                        text("Disconnected", language)
                    )

                    # A refused browser request is visible and does not prevent a retry.
                    if tool == "desktop":
                        await page.locator("[data-remote-workspace]").evaluate(
                            "root => root.requestFullscreen = () => Promise.reject(Error('fixture refusal'))"
                        )
                        await page.get_by_role(
                            "button", name=text("Fullscreen", language), exact=True
                        ).click()
                        await expect(
                            page.locator("[data-remote-fullscreen-error]")
                        ).to_be_visible()
                        await page.locator("[data-remote-workspace]").evaluate(
                            "root => delete root.requestFullscreen"
                        )

                    await fullscreen(page, True, language)
                    await expect(
                        page.locator("[data-remote-fullscreen-error]")
                    ).to_be_hidden()
                    await fullscreen(page, False, language)
                    await fullscreen(page, True, language)
                    await connect.click()

                    if language == "en" and tool == "terminal":
                        await expect(page.get_by_role("status")).to_have_text(
                            "The remote viewer could not be loaded."
                        )
                        await expect(
                            page.get_by_role(
                                "button", name="Exit fullscreen", exact=True
                            )
                        ).to_be_visible()
                        await connect.click()

                    await expect(page.get_by_role("status")).to_have_text(
                        text("Connected", language)
                    )
                    await page.get_by_role(
                        "button", name=text("Disconnect", language), exact=True
                    ).click()
                    await expect(page.get_by_role("status")).to_have_text(
                        text("Disconnected", language)
                    )
                    await page.wait_for_function("() => !!document.fullscreenElement")
                    await fullscreen(page, False, language)

                    for width in [1440, 768, 390]:
                        await page.set_viewport_size(
                            {"width": width, "height": 1000 if width > 390 else 844}
                        )
                        await fullscreen(page, True, language)
                        assert await page.evaluate(
                            "document.documentElement.scrollWidth <= innerWidth"
                        )
                        if args.screenshots:
                            args.screenshots.mkdir(parents=True, exist_ok=True)
                            await page.screenshot(
                                path=str(
                                    args.screenshots
                                    / f"{tool}-{language}-{width}-disconnected.png"
                                )
                            )
                        await fullscreen(page, False, language)

                    await connect.click()
                    await expect(page.get_by_role("status")).to_have_text(
                        text("Connected", language)
                    )
                    if tool != "desktop":
                        await fullscreen(page, True, language)
                        await terminal_sockets[-1].close()
                        await expect(page.get_by_role("status")).to_have_text(
                            text("Disconnected", language)
                        )
                        await expect(page.locator(".xterm")).to_have_count(0)
                        await fullscreen(page, False, language)
                        await connect.click()
                        await expect(page.get_by_role("status")).to_have_text(
                            text("Connected", language)
                        )

                    # Exiting through browser UI/API also updates the same control.
                    await fullscreen(page, True, language)
                    await page.evaluate("document.exitFullscreen()")
                    await expect(
                        page.locator("[data-remote-fullscreen]")
                    ).to_have_attribute("aria-pressed", "false")
                    await fullscreen(page, True, language)
                    await page.get_by_role(
                        "link", name=text("Back to device", language), exact=True
                    ).click()
                    await expect(page).to_have_url(base + "/devices/" + agent.identity)
                    await page.wait_for_function("() => !document.fullscreenElement")
                    print(
                        f"PASS {language} {tool}: disconnected controls, enter/exit/reconnect, fullscreen failure/recovery, transport close, responsive fit and navigation cleanup",
                        flush=True,
                    )

                for tab in ["Desktop", "Terminal"]:
                    await ready(page, base + "/devices/" + agent.identity)
                    await page.get_by_role(
                        "button", name=text(tab, language), exact=True
                    ).click()
                    if tab == "Terminal":
                        await page.get_by_role(
                            "button", name=text("Connect", language), exact=True
                        ).click()
                    await expect(page.get_by_role("status")).to_have_text(
                        text("Connected", language)
                    )
                    await fullscreen(page, True, language)
                    await page.get_by_role(
                        "button", name=text("Disconnect", language), exact=True
                    ).click()
                    await expect(page.get_by_role("status")).to_have_text(
                        text("Disconnected", language)
                    )
                    await fullscreen(page, False, language)
                    await fullscreen(page, True, language)
                    await fullscreen(page, False, language)
                    await expect(page).to_have_url(base + "/devices/" + agent.identity)
                print(
                    f"PASS {language} inline Desktop/Terminal: fullscreen exit and re-entry stay available after disconnect",
                    flush=True,
                )

            assert not errors, errors
        finally:
            if agent:
                await agent.stop()
            await ready(page, base + "/enroll")
            if await page.locator("html").get_attribute("lang") == "fr":
                await select_language(page, "fr", "en")
            if agent:
                await ready(page, base + "/devices/" + agent.identity)
                page.once("dialog", lambda dialog: dialog.accept())
                await page.get_by_role(
                    "button", name="Remove device", exact=True
                ).click()
                await page.wait_for_url(base + "/devices")
            if created_key:
                await ready(page, base + "/enroll")
                await page.get_by_role("row").filter(
                    has=page.get_by_role("cell", name=name, exact=True)
                ).get_by_role("button", name="Revoke", exact=True).click()
            await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--auth-state", required=True)
    parser.add_argument("--screenshots", type=Path)
    asyncio.run(main(parser.parse_args()))
