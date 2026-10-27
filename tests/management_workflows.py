"""Native-first tools, Bash, shared dropdowns and combined logs on a disposable instance."""

import argparse
import asyncio
import json
import secrets
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from playwright.async_api import async_playwright, expect
from localization import ready, select_language, text
from ui_regressions import DesktopAgent


class LinuxAgent(DesktopAgent):
    async def inventory(self):
        await self.socket.send(
            json.dumps(
                {
                    "kind": "inventory",
                    "payload": {
                        "hostname": self.hostname,
                        "platform": "Linux",
                        "os": "Disposable Linux fixture",
                        "architecture": "x64",
                        "username": "fixture",
                        "ip": "192.0.2.10",
                        "mac": "",
                        "cores": 4,
                        "memoryTotal": 8000000000,
                        "memoryUsed": 2000000000,
                        "cpu": 10,
                        "uptime": 100,
                        "version": "test",
                        "capabilities": ["command"],
                        "disks": [],
                    },
                }
            )
        )


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
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        name = "Management-fixture-" + secrets.token_hex(5)
        script_name = name + ".sh"
        body = "values=(one two)\n[[ ${values[1]} == two ]] || exit 3\nprintf '%s\\n' \"${BASH_VERSION}\"\nexit 7\n"
        agent = None
        script_created = False
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
                data={"token": token, "name": name, "platform": "Linux"},
            )
            assert response.ok
            identity = await response.json()
            agent = LinuxAgent(base, identity["deviceId"], identity["secret"], name)
            await agent.start()

            await ready(page, base + "/scripts")
            await page.get_by_role("button", name="New script", exact=True).click()
            await page.get_by_label("Script name", exact=True).fill(name)
            await page.get_by_label("Script type", exact=True).select_option("bash")
            await expect(page.get_by_label("Platform", exact=True)).to_have_value(
                "Linux"
            )
            await expect(
                page.get_by_label("Platform", exact=True).locator(
                    'option[value="Windows"]'
                )
            ).to_have_count(0)
            await page.get_by_label("Script", exact=True).fill(body)
            await page.get_by_role("button", name="Save script", exact=True).click()
            await expect(page.get_by_text("Script saved.", exact=True)).to_be_visible()
            script_created = True
            await expect(
                page.get_by_role("cell", name=script_name, exact=True)
            ).to_be_visible()

            for language in ["en", "fr"]:
                await ready(page, base + "/devices/" + agent.identity)
                if language == "fr":
                    await select_language(page, "en", "fr")
                await page.get_by_role(
                    "button", name=text("Remote tools", language), exact=True
                ).click()
                categories = page.locator(".remote-tool-category")
                await expect(categories).to_have_count(2)
                await expect(categories.first.get_by_role("heading")).to_have_text(
                    text("Native tools", language)
                )
                native = await categories.first.locator(
                    ".remote-tool-copy strong"
                ).all_text_contents()
                assert native == [
                    text("Agent desktop", language),
                    text("Interactive terminal", language),
                    text("PowerShell terminal", language),
                ], native
                assert await page.locator(".remote-tool-card").first.evaluate(
                    "e => e.firstElementChild.classList.contains('stat-icon') && e.children[1].classList.contains('remote-tool-copy')"
                )
                for width in [1440, 768, 390]:
                    await page.set_viewport_size({"width": width, "height": 1000})
                    assert await page.evaluate(
                        "document.documentElement.scrollWidth <= innerWidth"
                    )
                    if args.screenshots:
                        args.screenshots.mkdir(parents=True, exist_ok=True)
                        await page.screenshot(
                            path=str(
                                args.screenshots / f"tools-{language}-{width}.png"
                            ),
                            full_page=True,
                        )
                await page.get_by_role(
                    "button", name=text("Commands", language), exact=True
                ).click()
                saved = page.get_by_label(text("Saved script", language), exact=True)
                option = saved.locator("option").filter(has_text=script_name)
                await saved.select_option(await option.get_attribute("value"))
                await expect(
                    page.get_by_label(text("Execution shell", language), exact=True)
                ).to_have_value("bash")
                for select in [
                    saved,
                    page.get_by_label(text("Execution shell", language), exact=True),
                ]:
                    metrics = await select.evaluate(
                        "e => ({padding:parseFloat(getComputedStyle(e).paddingRight),w:e.clientWidth,parent:e.closest('label').clientWidth,shared:e.parentElement.classList.contains('select-expand')})"
                    )
                    assert (
                        metrics["padding"] >= 28
                        and metrics["shared"]
                        and abs(metrics["w"] - metrics["parent"]) <= 2
                    ), metrics
                await page.get_by_role(
                    "button", name=text("Run command", language), exact=True
                ).click()
                await expect(page.locator(".terminal-output")).to_contain_text(
                    "Executed by disposable fixture"
                )
                assert agent.commands[-1]["shell"] == "bash"
                assert agent.commands[-1]["command"] == body

                await ready(page, base + "/logs")
                main_nav = page.get_by_role(
                    "navigation", name=text("Main navigation", language)
                )
                await expect(
                    main_nav.get_by_role(
                        "link", name=text("Logs", language), exact=True
                    )
                ).to_have_count(1)
                await expect(
                    main_nav.get_by_role(
                        "link", name=text("Sessions", language), exact=True
                    )
                ).to_have_count(0)
                tabs = page.get_by_role("navigation", name=text("Log views", language))
                await tabs.get_by_role(
                    "button", name=text("Audit log", language), exact=True
                ).click()
                await expect(page).to_have_url(base + "/logs?tab=audit")
                await expect(
                    page.get_by_role(
                        "table", name=text("Audit log", language), exact=True
                    )
                ).to_be_visible()
                await tabs.get_by_role(
                    "button", name=text("Sessions", language), exact=True
                ).click()
                await expect(
                    page.get_by_role(
                        "table", name=text("Sessions", language), exact=True
                    )
                ).to_be_visible()
                await ready(page, base + "/settings")
                await expect(
                    page.get_by_role(
                        "heading",
                        name=text("Your RDP credentials", language),
                        exact=True,
                    )
                ).to_have_count(0)
                await ready(page, base + "/account/security")
                await expect(
                    page.get_by_role(
                        "heading",
                        name=text("Your RDP credentials", language),
                        exact=True,
                    )
                ).to_be_visible()
                print(
                    f"PASS {language}: native-first icon/text cards, Bash save/selection, shared full-width selects, combined logs and RDP/security placement",
                    flush=True,
                )
            assert not errors, errors
        finally:
            await ready(page, base + "/scripts")
            if await page.locator("html").get_attribute("lang") == "fr":
                await select_language(page, "fr", "en")
            if script_created:
                page.once("dialog", lambda dialog: dialog.accept())
                await page.get_by_role("row").filter(
                    has=page.get_by_text(script_name, exact=True)
                ).get_by_role("button", name="Delete", exact=True).click()
                await expect(
                    page.get_by_text("Script deleted.", exact=True)
                ).to_be_visible()
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
