"""Selectable enrollment keys, shared installer budgets and a read-only download dialog."""

import argparse
import asyncio
import hashlib
from pathlib import Path
import secrets
import shutil
import struct
import subprocess
import tempfile

from playwright.async_api import async_playwright, expect
from localization import ready, select_language, text


def policy(executable):
    assert executable[-16:] == bytes.fromhex("b996015880544a19b7f7e9be44914c19")
    size = struct.unpack(">i", executable[-20:-16])[0]
    return executable[-20 - size : -20].decode("utf-8")


async def main(args):
    base = args.base_url.rstrip("/")
    names = [
        "Enrollment-fixture-" + secrets.token_hex(5) + suffix for suffix in ["-A", "-B"]
    ]
    created, devices = [], []
    language = "en"
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch()
        context = await browser.new_context(
            ignore_https_errors=True,
            storage_state=args.auth_state,
            viewport={"width": 1440, "height": 1050},
        )
        page = await context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))

        async def close():
            dialog = page.get_by_role("dialog")
            if await dialog.count():
                await page.keyboard.press("Escape")
                await expect(dialog).to_have_count(0)

        async def open_key(name):
            await close()
            button = page.get_by_role("button", name=name, exact=True)
            await button.click()
            dialog = page.get_by_role(
                "dialog", name=text("Enrollment key downloads", language), exact=True
            )
            await expect(dialog).to_be_visible()
            return dialog

        try:
            await ready(page, base + "/enroll")
            await expect(
                page.get_by_role("heading", name="Agent installation", exact=True)
            ).to_have_count(0)
            await expect(
                page.get_by_role("heading", name="Windows agent MSI", exact=True)
            ).to_have_count(0)
            await expect(
                page.get_by_label("Maximum devices", exact=True)
            ).to_have_count(0)
            await page.get_by_label("Key name", exact=True).fill(names[0])
            await page.get_by_label("Amount of uses", exact=True).fill("400")
            await page.get_by_label("Expires in (hours)", exact=True).fill("720")
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            dialog = page.get_by_role(
                "dialog", name="Enrollment key downloads", exact=True
            )
            await expect(dialog).to_be_visible()
            created.append(names[0])
            token_a = await dialog.locator("[data-enrollment-key]").input_value()
            await expect(
                dialog.get_by_label("Uses remaining", exact=True)
            ).to_have_value("400")
            await expect(dialog.get_by_label("Device group", exact=True)).to_have_value(
                "Unassigned"
            )
            assert (
                await dialog.locator("[data-enrollment-key]").get_attribute("readonly")
                is not None
            )
            exe = dialog.get_by_role("link", name="Windows x64 (.exe)", exact=True)
            msi = dialog.get_by_role("link", name="Windows x64 (.msi)", exact=True)
            assert await exe.get_attribute("class") == await msi.get_attribute("class")
            exe_uri, msi_uri = await exe.get_attribute("href"), await msi.get_attribute(
                "href"
            )
            assert token_a not in exe_uri and token_a not in msi_uri
            assert exe_uri.split("/executable")[0] == msi_uri.split("/msi")[0]
            anonymous = await playwright.request.new_context(ignore_https_errors=True)
            assert (await anonymous.get(base + exe_uri)).status == 401
            assert (await anonymous.get(base + msi_uri)).status == 401
            await anonymous.dispose()
            with tempfile.TemporaryDirectory(
                prefix="endpointcontrol-enrollment-"
            ) as folder:
                root = Path(folder)
                async with page.expect_download(timeout=90000) as info:
                    await exe.click()
                download = await info.value
                executable = root / download.suggested_filename
                await download.save_as(executable)
                expected_mesh = hashlib.sha384(token_a.encode()).hexdigest()
                assert "MeshID=0x" + expected_mesh in policy(executable.read_bytes())
                async with page.expect_download(timeout=90000) as info:
                    await msi.click()
                download = await info.value
                package = root / download.suggested_filename
                await download.save_as(package)
                assert package.read_bytes()[:8] == bytes.fromhex("d0cf11e0a1b11ae1")
                await asyncio.to_thread(
                    subprocess.run,
                    [
                        args.msiextract,
                        "--directory",
                        str(root / "payload"),
                        str(package),
                    ],
                    check=True,
                    capture_output=True,
                )
                inside = next((root / "payload").rglob("MeshAllAgent.exe"))
                assert "MeshID=0x" + expected_mesh in policy(inside.read_bytes())
                assert "MeshName=Unassigned" in policy(inside.read_bytes())
            await expect(
                dialog.get_by_label("Uses remaining", exact=True)
            ).to_have_value("400")
            await expect(page).to_have_url(base + "/enroll")
            print(
                "PASS per-key EXE/MSI downloads: same native credential/group, no secret in URLs, same 400-use budget, no download charge"
            )

            response = await context.request.post(
                base + "/api/agents/enroll",
                data={
                    "token": token_a,
                    "name": names[0] + "-device",
                    "platform": "Linux",
                },
            )
            assert response.ok
            devices.append((await response.json())["deviceId"])
            await close()
            await page.reload()
            await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            dialog = await open_key(names[0])
            assert (
                await dialog.locator("[data-enrollment-key]").input_value() == token_a
            )
            await expect(
                dialog.get_by_label("Uses remaining", exact=True)
            ).to_have_value("399")
            await close()
            await page.get_by_label("Key name", exact=True).fill(names[1])
            await page.get_by_label("Amount of uses", exact=True).fill("2")
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            dialog = page.get_by_role("dialog")
            await expect(dialog).to_be_visible()
            created.append(names[1])
            token_b = await dialog.locator("[data-enrollment-key]").input_value()
            assert token_a != token_b
            await close()
            dialog = await open_key(names[0])
            await expect(dialog.get_by_label("Key name", exact=True)).to_have_value(
                names[0]
            )
            assert (
                await dialog.locator("[data-enrollment-key]").input_value() == token_a
            )
            await expect(
                dialog.get_by_label("Uses remaining", exact=True)
            ).to_have_value("399")
            print(
                "PASS key selection after reload returns the matching read-only key, group and remaining uses"
            )

            for language in ["en", "fr"]:
                await close()
                if language == "fr":
                    await select_language(page, "en", "fr")
                    await page.locator("[data-meshall-ready=true]").wait_for(
                        state="attached"
                    )
                dialog = await open_key(names[0])
                for width in [1440, 768, 390]:
                    await page.set_viewport_size({"width": width, "height": 1050})
                    assert await dialog.evaluate("d => d.open && d.matches(':modal')")
                    assert await page.evaluate(
                        "document.documentElement.scrollWidth <= innerWidth + 1"
                    )
                    bounds = await dialog.bounding_box()
                    assert (
                        bounds["x"] >= 0 and bounds["x"] + bounds["width"] <= width + 1
                    )
                for _ in range(12):
                    await page.keyboard.press("Tab")
                    assert await page.evaluate(
                        "!!document.activeElement.closest('dialog')"
                    )
                await close()
                await expect(
                    page.get_by_role("button", name=names[0], exact=True)
                ).to_be_focused()
                print(
                    f"PASS {language}: read-only modal, responsive layout, focus trap, Escape and focus restoration"
                )

            for index in range(2):
                response = await context.request.post(
                    base + "/api/agents/enroll",
                    data={
                        "token": token_b,
                        "name": names[1] + str(index),
                        "platform": "Linux",
                    },
                )
                assert response.ok
                devices.append((await response.json())["deviceId"])
            dialog = await open_key(names[1])
            await expect(
                dialog.get_by_label(text("Uses remaining", language), exact=True)
            ).to_have_value("0")
            await expect(
                dialog.get_by_role("link", name="Windows x64 (.exe)", exact=True)
            ).to_have_count(0)
            await expect(
                dialog.get_by_role("button", name="Windows x64 (.exe)", exact=True)
            ).to_be_disabled()
            assert not errors, errors
            print(
                "PASS exhausted keys stay view-only and cannot produce new installers"
            )
        finally:
            await close()
            for identity in devices:
                await ready(page, base + "/devices/" + identity)
                page.once("dialog", lambda dialog: dialog.accept())
                await page.get_by_role(
                    "button", name=text("Remove device", language), exact=True
                ).click()
                await page.wait_for_url(base + "/devices")
            await ready(page, base + "/enroll")
            for key_name in created:
                row = page.get_by_role("row").filter(
                    has=page.get_by_role("cell", name=key_name, exact=True)
                )
                if await row.count():
                    await row.get_by_role(
                        "button", name=text("Revoke", language), exact=True
                    ).click()
                    await expect(row).to_have_count(0)
            await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--auth-state", required=True)
    parser.add_argument(
        "--msiextract", default=shutil.which("msiextract") or "/usr/bin/msiextract"
    )
    asyncio.run(main(parser.parse_args()))
