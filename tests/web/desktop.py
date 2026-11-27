"""Run with Python Playwright and its Chromium, or pass an installed browser executable."""
from pathlib import Path
import sys

from playwright.sync_api import expect, sync_playwright


PAGE = (Path(__file__).resolve().parents[2] / "windows/Web/index.html").as_uri()
FIXTURE = """
window.events = [];
async function fixture(path, options) {
  if (path === "/api/pair") return { status: 200, session: { token: "test" } };
  if (path === "/api/frame") {
    const canvas = document.createElement("canvas");
    canvas.width = 1280;
    canvas.height = 720;
    return { status: 200, image: await new Promise(done => canvas.toBlob(done, "image/jpeg")) };
  }
  if (path === "/api/input") window.events.push(options.body);
  return { status: 204 };
}
"""


def pair(page):
    page.goto(PAGE)
    page.locator("#pair-code").fill("12345678")
    page.locator("#pair-button").click()
    expect(page.locator("#viewport")).to_have_attribute("data-ready", "true")


with sync_playwright() as playwright:
    browser = playwright.chromium.launch(executable_path=sys.argv[1] if len(sys.argv) > 1 else None)
    desktop = browser.new_page(viewport={"width": 1280, "height": 720})
    desktop.add_init_script(FIXTURE + "window.PhoneRemoteMac = {request: fixture};")
    pair(desktop)
    expect(desktop.locator("#direct-mode")).to_have_attribute("aria-pressed", "true")
    screen = desktop.locator("#screen")
    bounds = screen.bounding_box()
    controls = desktop.locator(".controls").bounding_box()
    assert bounds["x"] + bounds["width"] <= controls["x"]
    assert abs(bounds["width"] / bounds["height"] - 16 / 9) < .01
    assert bounds["y"] + bounds["height"] <= 720
    screen.click()
    desktop.keyboard.type("Hi")
    desktop.keyboard.press("Control+a")
    desktop.keyboard.press("Enter")
    screen.click(button="right")
    desktop.mouse.wheel(0, 100)
    desktop.wait_for_function("events.some(e => e.action === 'scroll')")
    events = desktop.evaluate("events")
    actions = [e["action"] for e in events]
    assert actions.index("down") < actions.index("up")
    assert "rightClick" in actions
    assert "".join(e["text"] for e in events if e["action"] == "text") == "Hi"
    assert [e["key"] for e in events if e["action"] == "key"] == ["Ctrl+A", "Enter"]
    desktop.keyboard.press("Escape")
    expect(desktop.locator("#direct-mode")).to_be_focused()
    desktop.locator("#disconnect").click()
    expect(desktop.locator("#pair-panel")).to_be_visible()
    print("PASS desktop layout, direct mouse, keyboard, focus release and disconnect")

    phone = browser.new_page(viewport={"width": 390, "height": 844}, is_mobile=True, has_touch=True)
    phone.add_init_script(FIXTURE + """
      window.fetch = async (path, options) => {
        const result = await fixture(path, { body: options.body && JSON.parse(options.body) });
        return new Response(result.image || (result.session ? JSON.stringify(result.session) : null),
          { status: result.status });
      };
    """)
    pair(phone)
    expect(phone.locator("#direct-mode")).to_be_hidden()
    expect(phone.locator('[data-mode="click"]')).to_have_attribute("aria-pressed", "true")
    phone.locator("#screen").tap()
    phone.wait_for_function("events.some(e => e.action === 'click')")
    assert phone.locator(".controls").bounding_box()["y"] >= phone.locator("#viewport").bounding_box()["height"]
    assert phone.evaluate("document.documentElement.scrollWidth <= innerWidth")
    print("PASS narrow touch layout, pairing, frame decode and tap")
    browser.close()
