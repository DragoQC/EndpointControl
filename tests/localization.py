"""English/French copy, language persistence and localized workflows on a disposable instance."""

import argparse
import asyncio
import secrets
import xml.etree.ElementTree as ET
from pathlib import Path

from playwright.async_api import async_playwright, expect
from ui_regressions import DesktopAgent

RESOURCES = (
    Path(__file__).resolve().parents[1] / "src/MeshAll.Server/Resources/Localization"
)
FRENCH = {
    entry.attrib["name"]: entry.findtext("value")
    for entry in ET.parse(RESOURCES / "UiText.fr.resx").getroot().findall("data")
}
ROUTES = [
    ("/", "Dashboard", None),
    ("/devices", "Devices", None),
    ("/logs?tab=sessions", "Logs", None),
    ("/logs?tab=audit", "Logs", None),
    ("/scripts", "Scripts", "New script"),
    ("/users", "Users", "Add user"),
    ("/roles", "Roles", "Create role"),
    ("/groups", "Device groups", "New group"),
    ("/enroll", "Enrollment keys", None),
    ("/settings", "App settings", None),
    ("/account/security", "Account security", None),
    ("/install", "Server installation", None),
    ("/compatibility", "About EndpointControl", None),
]
BANNED = [
    "Your devices. One clear view.",
    "Every connection has a story.",
    "A place for everything.",
    "Self-hosted, by you",
]


def text(key, language):
    return FRENCH.get(key, key) if language == "fr" else key


async def ready(page, url):
    await page.goto(url)
    if "/account/security" not in url and "/remote/" not in url:
        await page.locator("[data-meshall-ready=true]").wait_for(state="attached")


async def select_language(page, current, target):
    await page.get_by_role(
        "combobox", name=text("Language", current), exact=True
    ).select_option(target)
    await expect(page.locator("html")).to_have_attribute("lang", target)
    await expect(
        page.get_by_role("combobox", name=text("Language", target), exact=True)
    ).to_have_value(target)


async def main(args):
    base = args.base_url.rstrip("/")
    screenshots = Path(args.screenshots) if args.screenshots else None
    if screenshots:
        screenshots.mkdir(parents=True, exist_ok=True)
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        anonymous = await browser.new_context(ignore_https_errors=True, locale="en-US")
        login = await anonymous.new_page()
        await login.goto(base + "/login")
        await expect(
            login.get_by_role("heading", name="Sign in", exact=True)
        ).to_be_visible()
        for phrase in BANNED:
            await expect(login.locator("body")).not_to_contain_text(phrase)
        await select_language(login, "en", "fr")
        await expect(
            login.get_by_role("heading", name="Se connecter", exact=True)
        ).to_be_visible()
        await expect(
            login.get_by_label("Nom d’utilisateur", exact=True)
        ).to_be_visible()
        await login.goto(base + "/login?failed=true")
        await expect(login.get_by_role("alert")).to_contain_text("Vérifiez")
        if screenshots:
            await login.screenshot(
                path=str(screenshots / "login-fr.png"), full_page=True
            )
        persisted = await anonymous.storage_state()
        second_browser = await browser.new_context(
            ignore_https_errors=True, storage_state=persisted
        )
        second_page = await second_browser.new_page()
        await second_page.goto(base + "/login")
        await expect(second_page.locator("html")).to_have_attribute("lang", "fr")
        await second_browser.close()
        await anonymous.close()
        print(
            "PASS concise login copy, French anonymous pages and persistent language selection",
            flush=True,
        )

        context = await browser.new_context(
            ignore_https_errors=True,
            storage_state=args.auth_state,
            locale="en-US",
            viewport={"width": 1440, "height": 1000},
        )
        page = await context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        name = "Localization-fixture-" + secrets.token_hex(5)
        agent = None
        script_name = name + ".ps1"
        created_script = False
        language = "en"
        try:
            await ready(page, base + "/enroll")
            await page.get_by_label("Key name", exact=True).fill(name)
            await page.get_by_role(
                "button", name="Create enrollment key", exact=True
            ).click()
            link = page.locator(".download-links a").first
            await link.wait_for()
            token = await page.locator("[data-enrollment-key]").input_value()
            response = await context.request.post(
                base + "/api/agents/enroll",
                data={"token": token, "name": name, "platform": "Windows"},
            )
            identity = await response.json()
            agent = DesktopAgent(base, identity["deviceId"], identity["secret"], name)
            await agent.start()

            await ready(page, base + "/scripts")
            await page.get_by_role("button", name="New script", exact=True).click()
            await page.get_by_label("Script name", exact=True).fill(script_name)
            await page.get_by_label("Script", exact=True).fill(
                "Write-Output 'RAW-SCRIPT-DATA'"
            )
            await page.get_by_role("button", name="Save script", exact=True).click()
            await expect(page.get_by_text("Script saved.", exact=True)).to_be_visible()
            created_script = True

            for language in ["en", "fr"]:
                if language == "fr":
                    await select_language(page, "en", "fr")
                for route, heading, action in ROUTES:
                    await ready(page, base + route)
                    await expect(page.locator("html")).to_have_attribute(
                        "lang", language
                    )
                    await expect(
                        page.get_by_role(
                            "heading", name=text(heading, language), exact=True, level=1
                        )
                    ).to_be_visible()
                    assert "fleet" not in (await page.title()).lower(), route
                    labels = await page.locator(
                        "h1, h2, h3, .stat-label, nav"
                    ).all_inner_texts()
                    assert not any("fleet" in label.lower() for label in labels), route
                    if action:
                        await page.get_by_role(
                            "button", name=text(action, language), exact=True
                        ).click()
                    if language == "fr":
                        labels = await page.locator(
                            "button, th, dt, h1, h2, h3, .stat-label, .status, .health-badge"
                        ).all_inner_texts()
                        untranslated = [
                            label.strip()
                            for label in labels
                            if label.strip() in FRENCH
                            and FRENCH[label.strip()] != label.strip()
                        ]
                        assert not untranslated, (route, untranslated)
                    for phrase in BANNED:
                        await expect(page.locator("body")).not_to_contain_text(phrase)
                    if screenshots:
                        filename = (
                            route.strip("/").replace("/", "-") or "dashboard"
                        ) + f"-{language}.png"
                        await page.screenshot(
                            path=str(screenshots / filename), full_page=True
                        )
                print(
                    "PASS localized navigation, pages and editors:",
                    language,
                    flush=True,
                )

            await ready(page, base + "/scripts")
            await page.get_by_role("button", name="Nouveau script", exact=True).click()
            await page.get_by_label("Type de script", exact=True).select_option("auto")
            await page.get_by_label("Plateforme", exact=True).select_option("Any")
            await expect(page.get_by_label("Plateforme", exact=True)).to_have_value(
                "Any"
            )
            await expect(
                page.get_by_label("Plateforme", exact=True).locator("option:checked")
            ).to_have_text("Tous")
            await page.get_by_role("button", name="Annuler", exact=True).click()
            confirmation = asyncio.Event()
            confirmation_text = []

            async def dismiss_delete(dialog):
                confirmation_text.append(dialog.message)
                await dialog.dismiss()
                confirmation.set()

            page.once("dialog", dismiss_delete)
            await page.get_by_role("row").filter(
                has=page.get_by_text(script_name, exact=True)
            ).get_by_role("button", name="Supprimer", exact=True).click()
            await asyncio.wait_for(confirmation.wait(), timeout=10)
            assert confirmation_text == [f"Supprimer {script_name} ?"]

            await ready(page, base + "/devices/" + agent.identity)
            await expect(
                page.get_by_role("heading", name=name, exact=True)
            ).to_be_visible()
            await page.get_by_role("button", name="Commandes", exact=True).click()
            option = (
                page.get_by_label("Script enregistré", exact=True)
                .locator("option")
                .filter(has_text=script_name)
            )
            await page.get_by_label("Script enregistré", exact=True).select_option(
                await option.get_attribute("value")
            )
            await expect(page.get_by_label("Interpréteur", exact=True)).to_have_value(
                ".ps1"
            )
            await page.get_by_role(
                "button", name="Exécuter la commande", exact=True
            ).click()
            await expect(page.locator(".terminal-output")).to_contain_text("[sortie 0]")
            await expect(page.locator(".terminal-output")).to_contain_text(
                "Executed by disposable fixture"
            )
            assert agent.commands[-1]["shell"] == ".ps1"
            print(
                "PASS French script controls preserve internal types, custom names and raw command output",
                flush=True,
            )

            await ready(
                page, base + "/remote/" + agent.identity + "/desktop?connect=true"
            )
            await expect(page.get_by_role("status")).to_have_text(
                "Connecté", timeout=30000
            )
            await page.get_by_role("button", name="Déconnecter", exact=True).click()
            await expect(page.get_by_role("status")).to_have_text("Déconnecté")
            print("PASS localized remote-viewer connection states", flush=True)

            for width in [1440, 768, 390]:
                await page.set_viewport_size({"width": width, "height": 1000})
                await ready(page, base + "/")
                await expect(
                    page.get_by_role("combobox", name="Langue", exact=True)
                ).to_be_visible()
                assert await page.evaluate(
                    "document.documentElement.scrollWidth<=innerWidth+1"
                )
                await page.evaluate("document.documentElement.dataset.theme='dark'")
                if screenshots:
                    await page.screenshot(
                        path=str(screenshots / f"dashboard-fr-dark-{width}.png"),
                        full_page=True,
                    )
            await select_language(page, "fr", "en")
            language = "en"
            await expect(
                page.get_by_role("heading", name="Dashboard", exact=True)
            ).to_be_visible()
            assert not errors, errors
            print(
                "PASS language switching, static security pages, mobile/tablet layout and dark theme",
                flush=True,
            )
        finally:
            if agent:
                await agent.stop()
            if created_script:
                await ready(page, base + "/scripts")
                page.once("dialog", lambda dialog: dialog.accept())
                row = page.get_by_role("row").filter(
                    has=page.get_by_text(script_name, exact=True)
                )
                await row.get_by_role(
                    "button", name=text("Delete", language), exact=True
                ).click()
                await expect(row).to_have_count(0)
            if agent:
                await ready(page, base + "/devices/" + agent.identity)
                page.once("dialog", lambda dialog: dialog.accept())
                await page.get_by_role(
                    "button", name=text("Remove device", language), exact=True
                ).click()
                await ready(page, base + "/enroll")
                await page.get_by_role("row").filter(
                    has=page.get_by_role("cell", name=name, exact=True)
                ).get_by_role(
                    "button", name=text("Revoke", language), exact=True
                ).click()
            await browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--auth-state", required=True)
    parser.add_argument("--screenshots")
    asyncio.run(main(parser.parse_args()))
