"""Browser regressions on a disposable instance using a simulated desktop agent."""

import argparse
import asyncio
import base64
import io
import json
import secrets
import ssl
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from PIL import Image, ImageDraw
from playwright.async_api import async_playwright, expect
from websockets.asyncio.client import connect


class DesktopAgent:
    def __init__(self, base, identity, secret, hostname):
        self.base, self.identity, self.secret, self.hostname = (
            base,
            identity,
            secret,
            hostname,
        )
        self.inputs = []
        self.commands = []
        self.streams = {}
        self.image(1280, 800)

    def image(self, width, height):
        self.width, self.height = width, height
        frame = Image.new("RGB", (width, height), "#136c80")
        draw = ImageDraw.Draw(frame)
        draw.rectangle((width // 2, 0, width - 1, height // 2), fill="#cf9331")
        draw.rectangle((0, height // 2, width // 2, height - 1), fill="#57975c")
        draw.rectangle((width // 2, height // 2, width - 1, height - 1), fill="#7254a8")
        draw.text(
            (25, 25),
            f"MeshAll desktop viewport fixture {width} x {height}",
            fill="white",
        )
        buffer = io.BytesIO()
        frame.save(buffer, "PNG")
        self.encoded = base64.b64encode(buffer.getvalue()).decode()

    async def inventory(self):
        await self.socket.send(
            json.dumps(
                {
                    "kind": "inventory",
                    "payload": {
                        "hostname": self.hostname,
                        "platform": "Windows",
                        "os": "Disposable viewport fixture",
                        "architecture": "x64",
                        "username": "Fixture",
                        "ip": "127.0.0.1",
                        "mac": "",
                        "cores": 4,
                        "memoryTotal": 8000000000,
                        "memoryUsed": 2000000000,
                        "cpu": 10,
                        "uptime": 100,
                        "version": "UI test fixture",
                        "capabilities": ["desktop", "command", "files"],
                        "disks": [],
                    },
                }
            )
        )

    async def start(self):
        options = {
            "additional_headers": {
                "X-Device-Id": self.identity,
                "Authorization": "Bearer " + self.secret,
            }
        }
        if self.base.startswith("https:"):
            options["ssl"] = ssl._create_unverified_context()
        self.socket = await connect(
            self.base.replace("https:", "wss:").replace("http:", "ws:")
            + "/api/agents/connect",
            **options,
        )
        await self.inventory()

        async def receive():
            async for raw in self.socket:
                message = json.loads(raw)
                if message.get("kind") == "stream":
                    payload = message["payload"]
                    writer, _ = self.streams[payload["sessionId"]]
                    writer.write(base64.b64decode(payload["base64"]))
                    await writer.drain()
                    continue
                if message.get("kind") != "request":
                    continue
                operation, payload = message["operation"], message.get("payload", {})
                if operation == "desktop.open":
                    result = {"sessionId": payload["sessionId"]}
                elif operation == "desktop.frame":
                    result = {
                        "base64": self.encoded,
                        "width": self.width,
                        "height": self.height,
                    }
                elif operation == "desktop.input":
                    self.inputs.append(payload["input"])
                    result = "Received"
                elif operation == "command":
                    self.commands.append(payload)
                    result = {
                        "output": "Executed by disposable fixture",
                        "exitCode": 0,
                        "directory": "/fixture",
                        "truncated": False,
                    }
                elif operation == "tcp.open":
                    assert payload["host"] == "127.0.0.1"
                    reader, writer = await asyncio.open_connection(
                        "127.0.0.1", payload["port"]
                    )
                    session_id = payload["sessionId"]

                    async def stream_output(reader, session_id):
                        while data := await reader.read(32768):
                            await self.socket.send(
                                json.dumps(
                                    {
                                        "kind": "stream",
                                        "payload": {
                                            "sessionId": session_id,
                                            "base64": base64.b64encode(data).decode(),
                                        },
                                    }
                                )
                            )
                        await self.socket.send(
                            json.dumps({"kind": "stream.end", "id": session_id})
                        )

                    self.streams[session_id] = (
                        writer,
                        asyncio.create_task(stream_output(reader, session_id)),
                    )
                    result = {"sessionId": session_id}
                elif operation == "stream.close":
                    stream = self.streams.pop(payload["sessionId"], None)
                    if stream:
                        writer, output = stream
                        writer.close()
                        output.cancel()
                        await asyncio.gather(output, return_exceptions=True)
                    result = "Closed"
                else:
                    result = "Completed"
                await self.socket.send(
                    json.dumps(
                        {
                            "kind": "result",
                            "id": message["id"],
                            "payload": result,
                            "success": True,
                        }
                    )
                )

        async def pulse():
            while True:
                await asyncio.sleep(1)
                await self.inventory()

        self.reader = asyncio.create_task(receive())
        self.heartbeat = asyncio.create_task(pulse())

    async def stop(self):
        self.heartbeat.cancel()
        for writer, output in self.streams.values():
            writer.close()
            output.cancel()
        await asyncio.gather(
            *(output for _, output in self.streams.values()), return_exceptions=True
        )
        self.streams.clear()
        await self.socket.close()
        await asyncio.gather(self.reader, self.heartbeat, return_exceptions=True)


async def assert_fitted(page, width, height, inline=False):
    await page.wait_for_function(
        "([w,h])=>{const c=document.getElementById('remote-canvas');return c?.width===w&&c.height===h}",
        arg=[width, height],
    )
    if inline:
        await page.locator("#remote-canvas").scroll_into_view_if_needed()
    await page.wait_for_timeout(150)
    metrics = await page.locator("#remote-canvas").evaluate(
        """c=>{const r=c.getBoundingClientRect(),s=c.parentElement;return{
        x:r.x,y:r.y,right:r.right,bottom:r.bottom,width:r.width,height:r.height,
        availableWidth:s.clientWidth,availableHeight:s.clientHeight,
        pageHeight:document.documentElement.scrollHeight,pageWidth:document.documentElement.scrollWidth,
        viewportHeight:innerHeight,viewportWidth:innerWidth,stageHeight:s.scrollHeight,stageWidth:s.scrollWidth}}"""
    )
    assert metrics["x"] >= 0 and metrics["y"] >= 0, metrics
    assert (
        metrics["right"] <= metrics["viewportWidth"] + 1
        and metrics["bottom"] <= metrics["viewportHeight"] + 1
    ), metrics
    assert (
        inline or metrics["pageHeight"] <= metrics["viewportHeight"] + 1
    ) and metrics["pageWidth"] <= metrics["viewportWidth"] + 1, metrics
    assert (
        metrics["stageHeight"] <= metrics["availableHeight"] + 1
        and metrics["stageWidth"] <= metrics["availableWidth"] + 1
    ), metrics
    scale = min(metrics["availableWidth"] / width, metrics["availableHeight"] / height)
    assert (
        abs(metrics["width"] - width * scale) < 2
        and abs(metrics["height"] - height * scale) < 2
    ), metrics
    return metrics


async def main(args):
    base = args.base_url.rstrip("/")
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        context = await browser.new_context(
            ignore_https_errors=args.allow_self_signed,
            storage_state=args.auth_state,
            viewport={"width": 1440, "height": 1000},
        )
        page = await context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        await page.goto(base + "/enroll")
        await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
        name = "Viewport-fixture-" + secrets.token_hex(4)
        await page.get_by_label("Key name", exact=True).fill(name)
        await page.get_by_role(
            "button", name="Create enrollment key", exact=True
        ).click()
        link = page.locator(".download-links a").first
        await link.wait_for()
        token = parse_qs(urlparse(await link.get_attribute("href")).query)["token"][0]
        response = await context.request.post(
            base + "/api/agents/enroll",
            data={"token": token, "name": name, "platform": "Windows"},
        )
        assert response.ok, await response.text()
        identity = await response.json()
        agent = DesktopAgent(base, identity["deviceId"], identity["secret"], name)
        await agent.start()
        try:
            await page.goto(base + "/devices/" + agent.identity)
            await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await expect(page.locator(".device-title .status")).to_have_text("Online")
            await page.get_by_role("button", name="Desktop", exact=True).click()
            await expect(page).to_have_url(base + "/devices/" + agent.identity)
            await expect(page.get_by_role("status")).to_have_text("Connected")
            await expect(page.locator("iframe")).to_have_count(0)
            for viewport in [
                (1440, 1000),
                (1100, 700),
                (900, 620),
                (550, 820),
                (390, 844),
            ]:
                await page.set_viewport_size(
                    {"width": viewport[0], "height": viewport[1]}
                )
                await assert_fitted(page, 1280, 800, inline=True)
            agent.image(800, 1280)
            await assert_fitted(page, 800, 1280, inline=True)
            await page.get_by_role("button", name="Fullscreen", exact=True).click()
            await page.wait_for_function(
                "() => document.fullscreenElement?.matches('[data-remote-workspace]')"
            )
            await assert_fitted(page, 800, 1280)
            await page.evaluate("document.exitFullscreen()")
            await page.get_by_role(
                "checkbox", name="Enable keyboard & mouse", exact=True
            ).check()
            metrics = await assert_fitted(page, 800, 1280, inline=True)
            await page.locator("#remote-canvas").click(
                position={"x": metrics["width"] * 0.75, "y": metrics["height"] * 0.4}
            )
            for _ in range(40):
                if any(event["type"] == "down" for event in agent.inputs):
                    break
                await asyncio.sleep(0.1)
            pressed = next(event for event in agent.inputs if event["type"] == "down")
            assert (
                abs(pressed["x"] - 0.75) < 0.01 and abs(pressed["y"] - 0.4) < 0.01
            ), pressed
            if args.screenshots:
                args.screenshots.mkdir(parents=True, exist_ok=True)
                await page.screenshot(path=str(args.screenshots / "fitted-desktop.png"))
            await page.get_by_role("button", name="Disconnect", exact=True).click()
            await expect(page.get_by_role("status")).to_have_text("Disconnected")
            await page.get_by_role("button", name="Overview", exact=True).click()
            await expect(page.locator("[data-remote-workspace]")).to_have_count(0)
            await page.get_by_role("button", name="Desktop", exact=True).click()
            await expect(page.get_by_role("status")).to_have_text("Connected")
            await assert_fitted(page, 800, 1280, inline=True)
            await page.goto(
                base + "/remote/" + agent.identity + "/desktop?connect=true"
            )
            await expect(page.get_by_role("status")).to_have_text("Connected")
            await assert_fitted(page, 800, 1280)
            await page.get_by_role("button", name="Disconnect", exact=True).click()
            await page.get_by_role("link", name="Back to device", exact=True).click()
            await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await page.set_viewport_size({"width": 1440, "height": 1000})
            print(
                "PASS inline and standalone desktop, tab return, auto-connect, responsive/portrait resize, fullscreen and scaled pointer coordinates",
                flush=True,
            )

            for theme in ["light", "dark"]:
                await page.evaluate(
                    "t=>{localStorage.setItem('meshall-theme',t);meshAll.restoreTheme()}",
                    theme,
                )
                for destination in [
                    "App settings",
                    "Devices",
                    "Security",
                    "Devices",
                    "App settings",
                    "Dashboard",
                ]:
                    await page.get_by_role(
                        "navigation", name="Main navigation"
                    ).get_by_role("link", name=destination, exact=True).click()
                    await expect(page.locator("html")).to_have_attribute(
                        "data-theme", theme
                    )
                    await expect(page.locator("body")).to_have_css(
                        "background-color",
                        "rgb(16, 27, 39)" if theme == "dark" else "rgb(243, 246, 249)",
                    )
                await page.get_by_role("link", name="Security", exact=True).click()
                await page.get_by_role(
                    "button", name="Toggle light or dark theme", exact=True
                ).click()
                await expect(page.locator("html")).to_have_attribute(
                    "data-theme", "light" if theme == "dark" else "dark"
                )
            print(
                "PASS light/dark navigation through App settings and static Security, including theme switching on Security",
                flush=True,
            )

            async def remove():
                await page.goto(base + "/devices/" + agent.identity)
                await page.locator("[data-meshall-ready=true]").wait_for(
                    state="attached"
                )
                page.once("dialog", lambda dialog: dialog.accept())
                await page.get_by_role(
                    "button", name="Remove device", exact=True
                ).click()
                await page.wait_for_url(base + "/devices")
                await expect(
                    page.locator(f'a[href="/devices/{agent.identity}"]')
                ).to_have_count(0)

            await remove()
            await page.wait_for_timeout(2500)
            await expect(
                page.locator(f'a[href="/devices/{agent.identity}"]')
            ).to_have_count(0)
            await agent.stop()
            await agent.start()
            await page.reload()
            await expect(
                page.locator(f'a[href="/devices/{agent.identity}"]').first
            ).to_be_visible()
            await agent.stop()
            await remove()
            await page.goto(base + "/enroll")
            await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await page.get_by_role("row").filter(
                has=page.get_by_role("cell", name=name, exact=True)
            ).get_by_role("button", name="Revoke", exact=True).click()
            print(
                "PASS device removal remains hidden during heartbeat, returns on authenticated reconnect, and fixture cleanup",
                flush=True,
            )
        finally:
            await agent.stop()
            await browser.close()
        assert not errors, errors


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True, help="Disposable test instance")
    parser.add_argument(
        "--auth-state", required=True, help="Private administrator browser state"
    )
    parser.add_argument("--allow-self-signed", action="store_true")
    parser.add_argument("--screenshots", type=Path)
    asyncio.run(main(parser.parse_args()))
