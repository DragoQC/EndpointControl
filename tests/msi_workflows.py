"""Enrollment-ready MSI downloads and GPO instructions on a disposable instance."""

import argparse
import asyncio
from pathlib import Path
import secrets
import tempfile

from playwright.async_api import async_playwright, expect
from localization import ready, select_language, text


async def main(args):
    base = args.base_url.rstrip("/")
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch()
        context = await browser.new_context(
            ignore_https_errors=True, storage_state=args.auth_state
        )
        page = await context.new_page()
        name = "MSI-fixture-" + secrets.token_hex(6)
        created = False
        language = "en"
        try:
            await ready(page, base + "/enroll")
            await page.get_by_label("Key name", exact=True).fill(name)
            await page.get_by_label("Maximum devices", exact=True).fill("1000")
            await page.get_by_label("Expires in (hours)", exact=True).fill("720")
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            link = page.get_by_role("link", name="Windows x64 (.msi)", exact=True)
            await expect(link).to_be_visible()
            created = True
            uri = await link.get_attribute("href")
            anonymous = await playwright.request.new_context(ignore_https_errors=True)
            response = await anonymous.get(base + uri)
            assert response.status == 401
            await anonymous.dispose()
            async with page.expect_download() as download_info:
                await link.click()
            response = await context.request.get(base + "/api/agents/windows-msi")
            assert response.status == 200
            assert "no-store" in response.headers["cache-control"]
            download = await download_info.value
            await expect(page).to_have_url(base + "/enroll")
            assert download.suggested_filename.endswith("-win-x64.msi")
            with tempfile.TemporaryDirectory(prefix="meshall-msi-download-") as folder:
                target = Path(folder) / download.suggested_filename
                await download.save_as(target)
                assert target.read_bytes()[:8] == bytes.fromhex("d0cf11e0a1b11ae1")
            row = page.get_by_role("row").filter(
                has=page.get_by_role("cell", name=name, exact=True)
            )
            await expect(
                row.get_by_role("cell", name="1000", exact=True)
            ).to_be_visible()
            print(
                "PASS authenticated self-contained MSI download, anonymous rejection, no-store and unconsumed deployment uses"
            )
            await ready(page, base + "/enroll")
            saved = page.get_by_role(
                "link", name="Download Windows x64 MSI", exact=True
            )
            await expect(saved).to_be_visible()
            assert await saved.get_attribute("href") == "/api/agents/windows-msi"
            async with page.expect_download():
                await saved.click()
            await expect(page).to_have_url(base + "/enroll")
            await page.reload()
            await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await page.get_by_label("Key name", exact=True).fill(name + "-layout")
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()

            for language in ["en", "fr"]:
                if language == "fr":
                    await select_language(page, "en", "fr")
                    await page.locator("[data-meshall-ready=true]").wait_for(
                        state="attached"
                    )
                    # A reload intentionally discards the raw enrollment token/download links.
                    await page.get_by_label(text("Key name", "fr"), exact=True).fill(
                        name + "-fr"
                    )
                    await page.get_by_role(
                        "button", name=text("Create enrollment key", "fr"), exact=True
                    ).click()
                    await expect(
                        page.get_by_role("link", name="Windows x64 (.msi)", exact=True)
                    ).to_be_visible()
                for width in [1440, 768, 390]:
                    await page.set_viewport_size({"width": width, "height": 1050})
                    assert await page.evaluate(
                        "document.documentElement.scrollWidth <= innerWidth + 1"
                    ), (language, width)
                    await expect(
                        page.locator(".deployment-command").first
                    ).to_contain_text("/qn /norestart")
                print(
                    f"PASS {language}: GPO instructions and MSI link at desktop/tablet/mobile widths"
                )
        finally:
            await ready(page, base + "/enroll")
            for key_name in [name, name + "-layout", name + "-fr"]:
                row = page.get_by_role("row").filter(
                    has=page.get_by_role("cell", name=key_name, exact=True)
                )
                if created and await row.count():
                    await row.get_by_role(
                        "button", name=text("Revoke", language), exact=True
                    ).click()
                    await expect(row).to_have_count(0)
            await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--auth-state", required=True)
    asyncio.run(main(parser.parse_args()))
