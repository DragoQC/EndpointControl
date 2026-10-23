"""Desktop recovery, VNC negotiation and typed-script UI on a disposable instance."""
import argparse
import asyncio
import io
import json
import secrets
import struct
from urllib.parse import parse_qs, urlparse
from PIL import Image
from playwright.async_api import async_playwright, expect
from ui_regressions import DesktopAgent


class RfbFixture:
    def __init__(self, password):
        self.password = password
        self.authenticated = False

    async def start(self):
        self.server = await asyncio.start_server(self.handle, "127.0.0.1", 0)
        self.port = self.server.sockets[0].getsockname()[1]

    async def handle(self, reader, writer):
        try:
            writer.write(b"RFB 003.008\n"); await writer.drain(); await reader.readexactly(12)
            writer.write(bytes([1, 2 if self.password else 1])); await writer.drain(); await reader.readexactly(1)
            if self.password:
                writer.write(bytes(range(16))); await writer.drain()
                response = await reader.readexactly(16)
                assert response != bytes(16)
            self.authenticated = True
            writer.write(bytes(4)); await writer.drain(); await reader.readexactly(1)
            name = b"MeshAll disposable VNC fixture"
            pixel_format = struct.pack(">BBBBHHHBBB3x", 32, 24, 0, 1, 255, 255, 255, 16, 8, 0)
            writer.write(struct.pack(">HH", 640, 400) + pixel_format + struct.pack(">I", len(name)) + name); await writer.drain()
            while True:
                kind = (await reader.readexactly(1))[0]
                if kind == 0:
                    await reader.readexactly(19)
                elif kind == 2:
                    header = await reader.readexactly(3)
                    await reader.readexactly(struct.unpack(">H", header[1:])[0] * 4)
                elif kind == 3:
                    await reader.readexactly(9)
                    pixels = b"".join((b"\x70\x40\xb0\x00" if y < 200 else b"\x20\xa0\x60\x00") * 640 for y in range(400))
                    writer.write(b"\x00\x00\x00\x01" + struct.pack(">HHHHi", 0, 0, 640, 400, 0) + pixels); await writer.drain()
                elif kind == 4:
                    await reader.readexactly(7)
                elif kind == 5:
                    await reader.readexactly(5)
                elif kind == 6:
                    header = await reader.readexactly(7)
                    await reader.readexactly(struct.unpack(">I", header[3:])[0])
                else:
                    return
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        finally:
            writer.close()

    async def stop(self):
        self.server.close(); await self.server.wait_closed()


async def main(args):
    base = args.base_url.rstrip("/")
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        context = await browser.new_context(ignore_https_errors=True, storage_state=args.auth_state)
        page = await context.new_page()
        await page.goto(base + "/enroll"); await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
        name = "Remote-workflows-" + secrets.token_hex(4)
        await page.get_by_label("Key name", exact=True).fill(name)
        await page.get_by_role("button", name="Create enrollment key", exact=True).click()
        link = page.locator(".download-links a").first; await link.wait_for()
        token = parse_qs(urlparse(await link.get_attribute("href")).query)["token"][0]
        response = await context.request.post(base + "/api/agents/enroll", data={"token": token, "name": name, "platform": "Windows"})
        identity = await response.json(); agent = DesktopAgent(base, identity["deviceId"], identity["secret"], name); await agent.start()
        sockets, packets, messages = [], [], []
        image = io.BytesIO(); Image.new("RGB", (640, 400), "#2b8a7f").save(image, "JPEG"); jpeg = image.getvalue()
        native = await browser.new_context(ignore_https_errors=True, storage_state=args.auth_state)
        await native.add_init_script("""(() => {let value;Object.defineProperty(window,'meshAll',{configurable:true,get(){return value;},set(api){value=api;const connect=api.remote.connect;api.remote.connect=function(options,ref){if(options.tool==='desktop')options.native=true;return connect.call(this,options,ref);};}});})();""")
        def route_socket(socket):
            sockets.append(socket)
            def message(data):
                messages.append(repr(data)[:150])
                if data == "2" or data == b"2":
                    socket.send(struct.pack(">HHHH", 7, 8, 640, 400))
                    socket.send(struct.pack(">HHHH", 3, len(jpeg) + 8, 0, 0) + jpeg)
                elif isinstance(data, bytes):
                    packets.append(data)
            socket.on_message(message); socket.send("c")
        await native.route_web_socket("**/api/remote/*/desktop", route_socket)
        np = await native.new_page();native_errors=[];np.on('pageerror',lambda error:native_errors.append(str(error))); await np.goto(f"{base}/remote/{agent.identity}/desktop?connect=true")
        try:await expect(np.get_by_role("status")).to_have_text("Connected", timeout=30000)
        except Exception:
            print('Native fixture errors:',native_errors,'messages:',messages[:10],'sockets:',len(sockets),'packets:',[list(packet[:12]) for packet in packets[:5]],'canvas:',await np.locator('#remote-canvas').evaluate('c=>({w:c.width,h:c.height,pixel:Array.from(c.getContext("2d").getImageData(0,0,1,1).data)})'),flush=True);raise
        control = np.get_by_role("checkbox", name="Enable keyboard & mouse", exact=True)
        await expect(control).to_be_checked(); await expect(control).to_be_enabled()
        await sockets[0].close()
        await expect(np.get_by_role("status")).to_have_text("Connected", timeout=15000)
        assert len(sockets) >= 2
        await expect(control).to_be_checked(); await expect(control).to_be_enabled()
        packets.clear(); await np.locator("#remote-canvas").click(position={"x": 100, "y": 80})
        await np.wait_for_timeout(200)
        assert any(len(packet) > 2 and int.from_bytes(packet[:2], "big") == 2 for packet in packets)
        before_switch=len(sockets)
        sockets[-1].send(json.dumps({"ctrlChannel":"102938","type":"meshall-desktop-changed"}))
        for _ in range(100):
            if len(sockets)>before_switch:break
            await asyncio.sleep(.1)
        await expect(np.get_by_role("status")).to_have_text("Connected",timeout=15000)
        assert len(sockets)>before_switch
        await expect(control).to_be_checked();await expect(control).to_be_enabled()
        sockets[-1].send(json.dumps({"ctrlChannel": "102938", "type": "meshall-desktop-ended", "reason": "local-user"}))
        await expect(np.get_by_role("status")).to_contain_text("person at the computer ended")
        before = len(sockets); await np.wait_for_timeout(1200); assert len(sockets) == before
        await native.close()
        print("PASS real native-viewer protocol recovery, automatic control rebinding, and local-user stop", flush=True)

        for kind, code in [(".cmd", "@echo off\necho %COMSPEC%\nexit /b 7"), (".ps1", "Write-Output $PSVersionTable.PSEdition\nexit 9")]:
            script_name = name + kind
            await page.goto(base + "/scripts"); await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await page.get_by_role("button", name="New script", exact=True).click()
            await page.get_by_label("Script name", exact=True).fill(script_name)
            await page.get_by_label("Script type", exact=True).select_option(kind)
            await page.get_by_label("Script", exact=True).fill(code)
            await page.get_by_role("button", name="Save script", exact=True).click(); await page.get_by_text("Script saved.", exact=True).wait_for()
            await page.goto(base + "/devices/" + agent.identity); await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            await page.get_by_role("button", name="Commands", exact=True).click()
            option = page.get_by_label("Saved script", exact=True).locator("option").filter(has_text=script_name)
            await page.get_by_label("Saved script", exact=True).select_option(await option.get_attribute("value"))
            await expect(page.get_by_label("Execution shell", exact=True)).to_have_value(kind)
            await page.get_by_role("button", name="Run command", exact=True).click()
            await expect(page.locator(".terminal-output")).to_contain_text("[exit 0]")
            assert agent.commands[-1]["shell"] == kind and agent.commands[-1]["command"] == code
            await page.goto(base + "/scripts"); await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
            page.once("dialog", lambda dialog: dialog.accept())
            await page.get_by_role("row").filter(has=page.get_by_text(script_name, exact=True)).get_by_role("button", name="Delete", exact=True).click()
        print("PASS .cmd/.ps1 editor, saved-script selection and interpreter request routing", flush=True)
        async with asyncio.timeout(15):await agent.stop()
        for password in [False, True]:
            fixture = RfbFixture(password); await fixture.start()
            try:
                print('Checking VNC negotiation:', 'password' if password else 'no authentication',flush=True)
                await page.goto(f"{base}/remote/{agent.identity}/vnc?port={fixture.port}")
                print('VNC page loaded',flush=True)
                if password:
                    await expect(page.get_by_role("heading", name="VNC authentication", exact=True)).to_be_visible()
                    print('VNC requested a password',flush=True)
                    await expect(page.get_by_label("VNC username", exact=True)).to_have_count(0)
                    await page.get_by_label("VNC password", exact=True).fill("fixture-password")
                    print('VNC password entered',flush=True)
                    await page.get_by_role("button", name="Continue", exact=True).click()
                    print('VNC password submitted',flush=True)
                await expect(page.get_by_role("status")).to_have_text("Connected", timeout=30000)
                assert fixture.authenticated
                await expect(page.get_by_label("Username", exact=True)).to_have_count(0)
                await page.get_by_role("button", name="Disconnect", exact=True).click()
            finally:
                await fixture.stop()
        print("PASS VNC auto-connect with no credentials and password prompt only when requested", flush=True)
        await page.goto(base + "/devices/" + agent.identity); await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
        page.once("dialog", lambda dialog: dialog.accept()); await page.get_by_role("button", name="Remove device", exact=True).click()
        await page.goto(base + "/enroll"); await page.locator("[data-meshall-ready=true]").wait_for(state="attached")
        await page.get_by_role("row").filter(has=page.get_by_role("cell", name=name, exact=True)).get_by_role("button", name="Revoke", exact=True).click()
        await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True, help="Disposable test instance")
    parser.add_argument("--auth-state", required=True)
    asyncio.run(main(parser.parse_args()))
