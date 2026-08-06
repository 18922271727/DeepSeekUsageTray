#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针：用 Edge 真实打开蓝奏云，验证登录态并探测上传页面结构（调试用）。"""
import json
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

from playwright.sync_api import sync_playwright

ROOT = os.path.dirname(os.path.abspath(__file__))
SESSION = os.path.join(ROOT, "session", "lanzou-session.json")
PROFILE = os.path.join(ROOT, ".pw-lanzou-profile")


def load_cookies():
    with open(SESSION, encoding="utf-8") as f:
        data = json.load(f)
    cookies = data.get("Cookies") or {}
    items = []
    for name, value in cookies.items():
        for domain in (".lanzou.com", ".woozooo.com"):
            items.append(
                {
                    "name": name,
                    "value": value,
                    "domain": domain,
                    "path": "/",
                }
            )
    return items


def main():
    if not os.path.exists(SESSION):
        print("SESSION_MISSING:", SESSION)
        return 1

    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(
            PROFILE,
            channel="msedge",
            headless=False,
            viewport={"width": 1280, "height": 860},
        )
        page = context.pages[0] if context.pages else context.new_page()

        try:
            context.add_cookies(load_cookies())
        except Exception as e:
            print("ADD_COOKIES_ERR:", e)

        page.goto("https://www.lanzou.com/", timeout=60000)
        time.sleep(6)
        print("URL:", page.url)
        print("TITLE:", page.title())
        text = page.inner_text("body")
        print("HAS_LOGOUT:", ("退出" in text))
        print("HAS_LOGIN_LINK:", ("登录" in text))
        print("HAS_AVATAR_MENU:", ("我的网盘" in text) or ("文件管理" in text))
        browser_cookies = {c["name"]: c["value"] for c in context.cookies()}
        print("BROWSER_ylogin:", browser_cookies.get("ylogin"))
        print("BODY_SNIPPET:", text[:400].replace("\n", " "))

        # 控制台页面（真实入口 https://up.woozooo.com/u）
        try:
            page.goto("https://up.woozooo.com/u", timeout=60000)
            time.sleep(6)
            print("CONSOLE_URL:", page.url)
            print("CONSOLE_TITLE:", page.title())
            file_inputs = page.query_selector_all("input[type=file]")
            print("FILE_INPUT_COUNT:", len(file_inputs))
            ct = page.inner_text("body")
            print("CONSOLE_TEXT:", ct[:900].replace("\n", " "))
            frames = [f.url for f in page.frames]
            print("FRAMES:", json.dumps(frames, ensure_ascii=False)[:400])
            buttons = page.eval_on_selector_all(
                "button, .btn, [class*=upload], [class*=share]",
                "els => els.slice(0, 30).map(e => ({text: e.innerText.trim(), cls: e.className}))",
            )
            print("BUTTONS:", json.dumps(buttons, ensure_ascii=False)[:800])
            if file_inputs:
                fi = file_inputs[0]
                print("INPUT_ATTRS:", fi.get_attribute("name"), fi.get_attribute("id"), fi.get_attribute("class"), fi.get_attribute("accept"))

            # 进入文件管理 iframe
            for f in page.frames:
                if "mydisk.php" in f.url and "item=files" in f.url:
                    print("\nFRAME:", f.url)
                    try:
                        f.wait_for_load_state("domcontentloaded", timeout=15000)
                    except Exception:
                        pass
                    time.sleep(3)
                    finputs = f.query_selector_all("input[type=file]")
                    print("FRAME_FILE_INPUT_COUNT:", len(finputs))
                    ftext = f.inner_text("body")
                    print("FRAME_TEXT:", ftext[:700].replace("\n", " "))
                    btns = f.eval_on_selector_all(
                        "button, a.btn, [class*=upload], [id*=upload], [class*=share]",
                        "els => els.slice(0, 40).map(e => ({text: e.innerText.trim(), cls: e.className, id: e.id}))",
                    )
                    print("FRAME_BUTTONS:", json.dumps(btns, ensure_ascii=False)[:1000])
                    rows = f.eval_on_selector_all(
                        "tr, .file-item, [class*=file]",
                        "els => els.slice(0, 5).map(e => ({cls: e.className, text: e.innerText.trim().slice(0, 200)}))",
                    )
                    print("FRAME_ROWS:", json.dumps(rows, ensure_ascii=False)[:800])
                    break
        except Exception as e:
            print("CONSOLE_PAGE_ERR:", e)

        context.close()
    return 0


if __name__ == "__main__":
    main()
