"""Device metrics, reusable tables and database paging on a disposable instance."""

import argparse
import asyncio
import json
import re
import secrets
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

from playwright.async_api import async_playwright, expect
from ui_regressions import DesktopAgent


class MetricsAgent(DesktopAgent):
    def __init__(self, base, identity, secret, name, metrics):
        super().__init__(base, identity, secret, name)
        self.metrics = metrics

    async def inventory(self):
        report = {
            "hostname": self.hostname,
            "platform": "Windows",
            "os": "Disposable metrics fixture",
            "architecture": "x64",
            "username": "Fixture",
            "ip": "192.0.2.10",
            "mac": "00:11:22:33:44:55",
            "uptime": 100,
            "version": "Browser fixture",
            "capabilities": ["desktop", "command"],
            **self.metrics,
        }
        await self.socket.send(json.dumps({"kind": "inventory", "payload": report}))


def seed_lists(database, prefix):
    now = datetime.now(timezone.utc)
    with sqlite3.connect(database, timeout=15) as connection:
        for index in range(263):
            connection.execute(
                "INSERT INTO sessions VALUES(?,?,?,?,?,?,NULL,'Active')",
                (
                    f"{prefix}-session-{index}",
                    "fixture",
                    f"{prefix}-device-{index:03}",
                    prefix,
                    "Command",
                    (now - timedelta(seconds=index)).isoformat(),
                ),
            )
        for index in range(128):
            connection.execute(
                "INSERT INTO audit(time,actor,action,target,detail) VALUES(?,?,?,?,?)",
                (
                    now.isoformat(),
                    prefix,
                    "Paging fixture",
                    f"{prefix}-audit-{index:03}",
                    "Fixture event",
                ),
            )
        for index in range(61):
            identity = f"{prefix}-list-{index:03}"
            connection.execute(
                "INSERT INTO users VALUES(?,?,?,'Viewer',1,1)",
                (identity, identity, "Non-login fixture hash"),
            )
            connection.execute(
                "INSERT INTO roles VALUES(?,?,'[]',0)", (identity, identity)
            )
            connection.execute(
                "INSERT INTO groups VALUES(?,?,'Paging fixture')", (identity, identity)
            )
            connection.execute(
                "INSERT INTO tokens VALUES(?,?,?,'default',?,1)",
                (identity, identity, identity, (now + timedelta(hours=1)).isoformat()),
            )
            connection.execute(
                "INSERT INTO scripts VALUES(?,?,'Windows','Write-Output fixture','.ps1')",
                (identity, identity + ".ps1"),
            )
        # An offline machine with extreme historic readings must not bias the averages.
        offline = {
            "hostname": "offline",
            "platform": "Windows",
            "os": "Offline fixture",
            "architecture": "x64",
            "username": "",
            "ip": "192.0.2.99",
            "mac": "",
            "cores": 64,
            "cpu": 100,
            "memoryTotal": 1000000,
            "memoryUsed": 999999,
            "uptime": 1,
            "version": "fixture",
            "capabilities": [],
            "disks": [{"name": "C:", "total": 1000000, "free": 0}],
            "cpuReportedAt": now.isoformat(),
            "memoryReportedAt": now.isoformat(),
            "disksReportedAt": now.isoformat(),
        }
        connection.execute(
            "INSERT INTO devices(id,name,groupId,secret,lastSeen,inventory) VALUES(?,?,'default','fixture',?,?)",
            (
                prefix + "-offline",
                prefix + "-offline",
                now.isoformat(),
                json.dumps(offline),
            ),
        )


def cleanup_lists(database, prefix):
    with sqlite3.connect(database, timeout=15) as connection:
        connection.execute("DELETE FROM sessions WHERE actor=?", (prefix,))
        connection.execute(
            "DELETE FROM audit WHERE actor=? OR target LIKE ?", (prefix, prefix + "%")
        )
        for table in ["users", "roles", "groups", "scripts", "devices"]:
            connection.execute(f"DELETE FROM {table} WHERE id LIKE ?", (prefix + "%",))
        token_ids = [
            row[0]
            for row in connection.execute(
                "SELECT id FROM tokens WHERE name LIKE ?", (prefix + "%",)
            )
        ]
        for identity in token_ids:
            connection.execute(
                "DELETE FROM native_enrollments WHERE tokenId=?", (identity,)
            )
            connection.execute("DELETE FROM tokens WHERE id=?", (identity,))


async def ready(page, url):
    await page.goto(url)
    await page.locator("[data-meshall-ready=true]").wait_for(state="attached")


async def main(args):
    base = args.base_url.rstrip("/")
    prefix = "Device-fixture-" + secrets.token_hex(5)
    agents = []
    identities = []
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
        await ready(page, base + "/")
        await expect(page.locator('[data-stat="Connected devices"]')).to_have_text("0")
        try:
            await ready(page, base + "/enroll")
            await page.get_by_label("Key name", exact=True).fill(prefix)
            await page.get_by_label("Amount of uses", exact=True).fill("3")
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            link = page.locator(".download-links a").first
            await link.wait_for()
            token = await page.locator("[data-enrollment-key]").input_value()
            metrics = [
                {
                    "cores": 2,
                    "cpu": 80,
                    "memoryTotal": 4000000000,
                    "memoryUsed": 1000000000,
                    "disks": [
                        {"name": "C:", "total": 100000000000, "free": 10000000000}
                    ],
                },
                {
                    "cores": 6,
                    "cpu": 20,
                    "memoryTotal": 12000000000,
                    "memoryUsed": 9000000000,
                    "disks": [
                        {"name": "/", "total": 900000000000, "free": 450000000000}
                    ],
                },
                {"cores": 4, "cpu": -1, "memoryTotal": 0, "memoryUsed": 0, "disks": []},
            ]
            for index, sample in enumerate(metrics):
                name = f"{prefix}-agent-{index}"
                response = await context.request.post(
                    base + "/api/agents/enroll",
                    data={"token": token, "name": name, "platform": "Windows"},
                )
                assert response.ok, await response.text()
                identity = await response.json()
                identities.append(identity["deviceId"])
                agent = MetricsAgent(
                    base, identity["deviceId"], identity["secret"], name, sample
                )
                await agent.start()
                agents.append(agent)

            seed_lists(args.database, prefix)
            await ready(page, base + "/")
            await expect(page.locator('[data-stat="Connected devices"]')).to_have_text(
                "3", timeout=20000
            )
            await expect(page.locator('[data-stat="Average CPU"]')).to_have_text("50%")
            await expect(page.locator('[data-stat="Average memory"]')).to_have_text(
                "50%"
            )
            await expect(page.locator('[data-stat="Average storage"]')).to_have_text(
                "70%"
            )
            assert "fleet" not in (await page.title()).lower()
            assert "fleet" not in (await page.locator("body").inner_text()).lower()
            await expect(
                page.locator('[data-stat="Nearly full devices"]')
            ).to_have_text("1")
            await expect(page.locator('[data-stat="Active sessions"]')).to_have_text(
                "263"
            )
            table = page.get_by_role("table", name="Connected devices", exact=True)
            await expect(table.locator("tbody tr")).to_have_count(3)
            await expect(table).not_to_contain_text(prefix + "-offline")
            await page.get_by_label("Resource filter", exact=True).select_option("disk")
            await expect(table.locator("tbody tr")).to_have_count(1)
            await expect(table).to_contain_text(prefix + "-agent-0")
            await page.get_by_label("Resource filter", exact=True).select_option("all")
            await page.get_by_label("Sort connected devices", exact=True).select_option(
                "cpu"
            )
            await expect(table.locator("tbody tr").first).to_contain_text(
                prefix + "-agent-0"
            )
            screenshots = Path(args.screenshots) if args.screenshots else None
            if screenshots:
                screenshots.mkdir(parents=True, exist_ok=True)
                await page.screenshot(
                    path=str(screenshots / "devices-light.png"), full_page=True
                )

            await ready(page, base + "/devices/" + identities[0])
            await expect(
                page.get_by_role("meter", name="CPU usage", exact=True)
            ).to_have_attribute("value", "80")
            await expect(
                page.get_by_role("meter", name="Memory usage", exact=True)
            ).to_have_attribute("value", "25")
            await expect(
                page.get_by_role("meter", name="Fullest disk", exact=True)
            ).to_have_attribute("value", "90")
            if screenshots:
                await page.screenshot(
                    path=str(screenshots / "device-light.png"), full_page=True
                )
            print(
                "PASS equal-device CPU/memory/storage averages, offline exclusion, capacity alerts and actual per-device values",
                flush=True,
            )

            await ready(page, base + "/sessions")
            await page.get_by_label("Search sessions", exact=True).fill(prefix)
            session_table = page.get_by_role("table", name="Sessions", exact=True)
            await expect(session_table.locator("tbody tr")).to_have_count(50)
            await expect(page.locator(".pagination-summary")).to_have_text(
                "1–50 of 263"
            )
            await page.get_by_role("button", name="Next", exact=True).click()
            await expect(page.get_by_label("Page number", exact=True)).to_have_value(
                "2"
            )
            await expect(session_table.locator("tbody tr").first).to_contain_text(
                prefix + "-device-050"
            )
            await page.get_by_label("Rows per page", exact=True).select_option("25")
            await expect(session_table.locator("tbody tr")).to_have_count(25)
            await expect(page.get_by_label("Page number", exact=True)).to_have_value(
                "1"
            )
            await page.get_by_label("Search sessions", exact=True).fill(
                prefix + "-device-262"
            )
            await expect(session_table.locator("tbody tr")).to_have_count(1)
            await expect(session_table).to_contain_text(prefix + "-device-262")
            print(
                "PASS session pages stay at 50 or fewer, navigation/size changes work, and search reaches beyond the former 250-row limit",
                flush=True,
            )

            await ready(page, base + "/audit")
            await page.get_by_label("Search audit log", exact=True).fill(
                prefix + "-audit-"
            )
            await expect(
                page.get_by_role("table", name="Audit log", exact=True).locator(
                    "tbody tr"
                )
            ).to_have_count(50)
            await expect(page.locator(".pagination-summary")).to_have_text(
                "1–50 of 128"
            )
            await page.get_by_role("button", name="Next", exact=True).click()
            await expect(page.get_by_label("Page number", exact=True)).to_have_value(
                "2"
            )
            for route, label in [
                ("users", "Workspace users"),
                ("roles", "Roles"),
                ("scripts", "Saved scripts"),
                ("enroll", "Enrollment keys"),
            ]:
                await ready(page, base + "/" + route)
                await expect(
                    page.get_by_role("table", name=label, exact=True).locator(
                        "tbody tr"
                    )
                ).to_have_count(50)
                await page.get_by_role("button", name="Next", exact=True).click()
                await expect(
                    page.get_by_label("Page number", exact=True)
                ).to_have_value("2")
                assert (
                    await page.get_by_role("table", name=label, exact=True)
                    .locator("tbody tr")
                    .count()
                    <= 50
                )
            await ready(page, base + "/groups")
            await expect(page.locator(".group-card")).to_have_count(50)
            await page.get_by_role("button", name="Next", exact=True).click()
            await expect(page.get_by_label("Page number", exact=True)).to_have_value(
                "2"
            )
            assert await page.locator(".group-card").count() <= 50
            print(
                "PASS shared database pagination for audit, users, roles, scripts, keys and group cards",
                flush=True,
            )

            for width in [1440, 390]:
                await page.set_viewport_size({"width": width, "height": 1000})
                await ready(page, base + "/")
                await page.evaluate(
                    "document.documentElement.dataset.theme='dark';localStorage.setItem('meshall-theme','dark')"
                )
                await page.wait_for_timeout(150)
                dimensions = await page.evaluate(
                    "({page:document.documentElement.scrollWidth,viewport:innerWidth})"
                )
                assert dimensions["page"] <= dimensions["viewport"] + 1, dimensions
                if screenshots:
                    await page.screenshot(
                        path=str(screenshots / f"devices-dark-{width}.png"),
                        full_page=True,
                    )
            print(
                "PASS dashboard layout at desktop/mobile sizes and dark theme",
                flush=True,
            )
            assert not errors, errors
        finally:
            for agent in agents:
                await agent.stop()
            await asyncio.sleep(0.2)
            cleanup_lists(args.database, prefix)
            with sqlite3.connect(args.database, timeout=15) as connection:
                for identity in identities:
                    connection.execute("DELETE FROM devices WHERE id=?", (identity,))
            await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--base-url",
        required=True,
        help="Disposable test instance with no connected agents",
    )
    parser.add_argument("--auth-state", required=True)
    parser.add_argument(
        "--database",
        required=True,
        type=Path,
        help="That disposable instance's SQLite database",
    )
    parser.add_argument("--screenshots")
    asyncio.run(main(parser.parse_args()))
