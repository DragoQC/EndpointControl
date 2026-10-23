"""Opt-in end-to-end checks against an enrolled native agent and real services."""

import argparse
import asyncio
import io
import os
from pathlib import Path
import secrets
import time

from PIL import Image
from playwright.async_api import async_playwright, expect


async def rendered(locator):
    """Inspect the composited page, not just the off-screen canvas dimensions."""
    deadline = time.monotonic() + 30
    while True:
        image = Image.open(io.BytesIO(await locator.screenshot())).convert("RGB")
        colors = image.resize((160, 100)).getcolors(16000)
        if colors and len(colors) > 32 and max(n for n, _ in colors) < 15920:
            return
        if time.monotonic() >= deadline:
            raise AssertionError("Remote display is blank in the composited browser page")
        await asyncio.sleep(0.5)


async def main(args):
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        context = await browser.new_context(
            ignore_https_errors=args.allow_self_signed,
            storage_state=args.auth_state,
            viewport={"width": 1440, "height": 1050},
            accept_downloads=True,
        )
        page = await context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        if args.renderer:
            await page.route("**/app.js", lambda route: route.fulfill(
                path=str(args.renderer.resolve()), content_type="application/javascript"))
        base = args.base_url.rstrip("/")
        if not args.auth_state:
            await page.goto(base + "/login")
            await page.locator('input[name="username"]').fill(os.environ["MESHALL_USERNAME"])
            await page.locator('input[name="password"]').fill(os.environ["MESHALL_PASSWORD"])
            await page.get_by_role("button", name="Sign in", exact=True).click()
            await page.wait_for_url(base + "/")

        async def device(tab):
            await page.goto(base + "/devices/" + args.device)
            await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await expect(page.locator(".device-title .status")).to_have_text("Online")
            await page.get_by_role("button", name=tab, exact=True).click()

        async def open_files(path):
            await device("Files")
            await page.get_by_role("textbox", name="Remote path", exact=True).fill(path)
            await page.get_by_role("button", name="Open path", exact=True).click()
            await page.get_by_role("columnheader", name="Modified", exact=True).wait_for()

        async def remove(button):
            page.once("dialog", lambda dialog: dialog.accept())
            await button.click()
            await page.get_by_text("Item deleted.", exact=True).wait_for()

        async def remote(tool):
            await page.goto(f"{base}/remote/{args.device}/{tool}")
            await expect(page.get_by_role("button", name="Connect", exact=True)).to_be_enabled()
            if tool in ("rdp", "vnc", "ssh"):
                if args.port:
                    await page.get_by_label("Remote port", exact=True).fill(str(args.port))
                await page.get_by_label("Username", exact=True).fill(os.environ.get("MESHALL_REMOTE_USERNAME", ""))
                await page.get_by_label("Password", exact=True).fill(os.environ.get("MESHALL_REMOTE_PASSWORD", ""))
                await page.get_by_role("checkbox", name="Connect directly from the orchestrator", exact=False).set_checked(args.direct)
            await page.get_by_role("button", name="Connect", exact=True).click()
            await expect(page.get_by_role("status")).to_have_text("Connected", timeout=90000)

        for check in args.checks:
            if check == "commands":
                await device("Commands")
                marker = "MeshAll-Command-" + secrets.token_hex(6)
                # Derive the suffix explicitly so the output cannot be mistaken for the echoed input.
                suffix = marker.removeprefix("MeshAll-Command-")
                command = f"Write-Output ('MeshAll-Command-' + '{suffix}')" if args.platform == "windows" else f"printf '%s%s\\n' 'MeshAll-Command-' '{suffix}'"
                await page.get_by_label("Command", exact=True).fill(command)
                await page.get_by_role("button", name="Run command", exact=True).click()
                await expect(page.locator(".terminal-output")).to_contain_text("[exit 0]", timeout=90000)
                await expect(page.locator(".terminal-output")).to_contain_text(marker)
            elif check == "processes":
                await device("Processes")
                await page.get_by_role("button", name="Refresh processes", exact=True).click()
                await expect(page.get_by_role("columnheader", name="PID", exact=True)).to_be_visible(timeout=90000)
                assert await page.locator("tbody tr").count() > 0
            elif check == "files":
                root = "C:/Windows/Temp" if args.platform == "windows" else "/tmp"
                folder = "MeshAll-Acceptance-" + secrets.token_hex(6)
                await open_files(root)
                await page.get_by_role("textbox", name="New folder name", exact=True).fill(folder)
                await page.get_by_role("button", name="Create folder", exact=True).click()
                await page.get_by_text("Folder created.", exact=True).wait_for(timeout=90000)
                await page.get_by_role("button", name=folder, exact=True).click()
                await expect(page.get_by_role("textbox", name="Remote path")).to_have_value(root + "/" + folder)
                payload = b"{\x00MeshAll-binary-round-trip\n" + bytes(range(256)) * 520
                upload = page.locator('input[type="file"]')
                await expect(upload).to_be_enabled()
                await upload.set_input_files({"name": "roundtrip.bin", "mimeType": "application/octet-stream", "buffer": payload})
                await page.get_by_text("File uploaded.", exact=True).wait_for(timeout=90000)
                async with page.expect_download(timeout=90000) as info:
                    await page.get_by_role("link", name="Download", exact=True).click()
                download = await info.value
                assert Path(await download.path()).read_bytes() == payload, "Binary download differs from upload"
                await remove(page.get_by_role("button", name="Delete", exact=True))
                await page.get_by_role("button", name="Up", exact=True).click()
                row = page.get_by_role("row").filter(has=page.get_by_role("button", name=folder, exact=True))
                await row.wait_for()
                await remove(row.get_by_role("button", name="Delete", exact=True))
                await expect(page.get_by_role("button", name=folder, exact=True)).to_have_count(0)
            elif check == "terminal":
                await remote("terminal")
                suffix = secrets.token_hex(6)
                marker = "MeshAll-Terminal-" + suffix
                command = f"echo MeshAll-Terminal-^{suffix}" if args.platform == "windows" else f"printf '%s%s\\n' 'MeshAll-Terminal-' '{suffix}'"
                terminal = page.locator(".xterm-helper-textarea")
                await terminal.press_sequentially(command, delay=15)
                await terminal.press("Enter")
                await expect(page.locator(".xterm-accessibility-tree")).to_contain_text(marker, timeout=30000)
                await page.get_by_role("button", name="Disconnect", exact=True).click()
            else:
                for attempt in range(2):
                    await remote(check)
                    display = page.locator("#remote-canvas" if check == "desktop" else "#remote-vnc")
                    if check == "desktop":
                        await page.wait_for_function('document.getElementById("remote-canvas").width > 300')
                    else:
                        await page.wait_for_function('Array.from(document.querySelectorAll("#remote-vnc canvas")).some(c => c.width > 300)')
                    await rendered(display)
                    if args.screenshots:
                        args.screenshots.mkdir(parents=True, exist_ok=True)
                        await page.screenshot(path=str(args.screenshots / f"{check}-{attempt + 1}.png"))
                    await page.get_by_role("button", name="Disconnect", exact=True).click()
                    await expect(page.get_by_role("status")).to_have_text("Disconnected")
            print(f"PASS {check}", flush=True)
        assert not errors, errors
        await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--platform", required=True, choices=("windows", "linux"))
    parser.add_argument("--checks", nargs="+", choices=("commands", "processes", "files", "terminal", "desktop", "rdp", "vnc", "ssh"), default=["commands", "processes", "files", "terminal"])
    parser.add_argument("--auth-state", help="Private Playwright storage state instead of login environment variables")
    parser.add_argument("--allow-self-signed", action="store_true")
    parser.add_argument("--direct", action="store_true", help="Use orchestrator TCP instead of the native TCP tunnel")
    parser.add_argument("--port", type=int)
    parser.add_argument("--renderer", type=Path, help="Test a local app.js before deployment")
    parser.add_argument("--screenshots", type=Path, help="Private output directory; screenshots can contain remote information")
    asyncio.run(main(parser.parse_args()))
