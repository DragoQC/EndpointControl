"""Masked, persistent per-user RDP defaults and responsive preferences on a disposable instance."""

import argparse
import asyncio
import secrets
from pathlib import Path

from playwright.async_api import async_playwright, expect
from localization import ready, select_language, text


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
        name = "RDP-defaults-fixture-" + secrets.token_hex(4)
        password = secrets.token_urlsafe(24)
        identity = None
        created_key = False
        language = "en"
        try:
            await ready(page, base + "/account/security")
            await expect(page.get_by_label("Password", exact=True)).to_be_enabled()
            await expect(page.get_by_label("Username", exact=True)).to_have_value("")
            await page.get_by_label("Domain", exact=True).fill("TEST-LAB")
            await page.get_by_label("Username", exact=True).fill("fixture-user")
            await page.get_by_label("Password", exact=True).fill(password)
            await expect(page.get_by_label("Password", exact=True)).to_have_attribute(
                "type", "password"
            )
            await page.get_by_role(
                "button", name="Save RDP credentials", exact=True
            ).click()
            await expect(page.locator(".notice.success")).to_have_text(
                "RDP credentials saved."
            )
            await page.reload()
            await expect(page.get_by_label("Password", exact=True)).to_have_value(
                password
            )
            await expect(page.get_by_label("Username", exact=True)).to_have_value(
                "fixture-user"
            )
            for path in ["/account/security", "/settings", "/api/audit/export"]:
                response = await context.request.get(base + path)
                assert response.ok, path
                assert password not in await response.text(), path

            await ready(page, base + "/enroll")
            await page.get_by_label("Key name", exact=True).fill(name)
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            link = page.locator(".download-links a").first
            await link.wait_for()
            token = await page.locator("[data-enrollment-key]").input_value()
            created_key = True
            response = await context.request.post(
                base + "/api/agents/enroll",
                data={"token": token, "name": name, "platform": "Windows"},
            )
            assert response.ok, await response.text()
            identity = (await response.json())["deviceId"]

            for language in ["en", "fr"]:
                await ready(page, base + "/account/security")
                if language == "fr":
                    await select_language(page, "en", "fr")
                await expect(
                    page.get_by_label(text("Password", language), exact=True)
                ).to_have_value(password)
                await expect(
                    page.get_by_role(
                        "heading",
                        name=text("Your RDP credentials", language),
                        exact=True,
                    )
                ).to_be_visible()
                await expect(page.locator("body")).not_to_contain_text("My workspace")
                await expect(page.locator(".workspace-label")).to_have_count(0)
                for width in [1440, 768, 390]:
                    await page.set_viewport_size(
                        {"width": width, "height": 1000 if width > 390 else 844}
                    )
                    assert await page.evaluate(
                        "document.documentElement.scrollWidth <= innerWidth"
                    ), (language, width)
                    select = page.get_by_role(
                        "combobox", name=text("Language", language), exact=True
                    )
                    assert await select.evaluate(
                        "e => parseFloat(getComputedStyle(e).paddingRight) >= 28"
                    )
                    if args.screenshots:
                        args.screenshots.mkdir(parents=True, exist_ok=True)
                        await page.screenshot(
                            path=str(
                                args.screenshots / f"security-{language}-{width}.png"
                            ),
                            full_page=True,
                        )

                # Check the same component embedded in the device page, with no iframe.
                await ready(page, base + "/devices/" + identity)
                await page.get_by_role(
                    "button", name=text("Remote tools", language), exact=True
                ).click()
                await page.get_by_role(
                    "button", name=text("Remote Desktop (RDP)", language)
                ).click()
                await expect(
                    page.get_by_label(text("Password", language), exact=True)
                ).to_have_value(password)
                await expect(
                    page.get_by_label(text("Password", language), exact=True)
                ).to_have_attribute("type", "password")
                await expect(
                    page.get_by_label(text("Username", language), exact=True)
                ).to_have_value("fixture-user")
                await expect(
                    page.get_by_label(text("Domain", language), exact=True)
                ).to_have_value("TEST-LAB")
                await expect(page).to_have_url(base + "/devices/" + identity)
                await expect(page.locator("iframe")).to_have_count(0)
                await page.get_by_role(
                    "link",
                    name=text("Manage your RDP credentials", language),
                    exact=True,
                ).click()
                await expect(page).to_have_url(
                    base + "/account/security#rdp-credentials"
                )

                await ready(page, base + "/remote/" + identity + "/rdp")
                await expect(
                    page.get_by_label(text("Password", language), exact=True)
                ).to_have_value(password)
                await expect(
                    page.get_by_label(text("Username", language), exact=True)
                ).to_have_value("fixture-user")
                await expect(
                    page.get_by_label(text("Domain", language), exact=True)
                ).to_have_value("TEST-LAB")

            await ready(page, base + "/account/security")
            await page.get_by_label(text("Username", language), exact=True).fill(
                "updated-user"
            )
            await page.get_by_label(text("Password", language), exact=True).fill("")
            await page.get_by_role(
                "button", name=text("Save RDP credentials", language), exact=True
            ).click()
            await expect(page.locator(".notice.success")).to_have_text(
                text("RDP credentials saved.", language)
            )
            await page.reload()
            await expect(
                page.get_by_label(text("Username", language), exact=True)
            ).to_have_value("updated-user")
            await expect(
                page.get_by_label(text("Password", language), exact=True)
            ).to_have_value("")
            await page.get_by_role(
                "button", name=text("Clear saved credentials", language), exact=True
            ).click()
            await expect(page.locator(".notice.success")).to_have_text(
                text("Saved RDP credentials cleared.", language)
            )
            await ready(page, base + "/remote/" + identity + "/rdp")
            await expect(
                page.get_by_label(text("Username", language), exact=True)
            ).to_be_enabled()
            await expect(
                page.get_by_label(text("Username", language), exact=True)
            ).to_have_value("")
            await expect(
                page.get_by_label(text("Password", language), exact=True)
            ).to_have_value("")
            print(
                "PASS masked RDP defaults beside two-factor settings: save/reload/update/clear, inline/standalone prefill, no HTML/audit leakage, EN/FR responsive security",
                flush=True,
            )
            assert not errors, errors
        finally:
            await ready(page, base + "/account/security")
            if await page.locator("html").get_attribute("lang") == "fr":
                await select_language(page, "fr", "en")
            clear = page.get_by_role(
                "button", name="Clear saved credentials", exact=True
            )
            if await clear.count():
                await clear.click()
                await expect(clear).to_have_count(0)
            if identity:
                await ready(page, base + "/devices/" + identity)
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
