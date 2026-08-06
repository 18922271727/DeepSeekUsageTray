#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从蓝奏云文件管理页抓取指定文件的分享链接（文件需已上传）。

用法:
    python lanzou_get_share_pw.py <文件名>

输出:
    OK / FILE_NOT_FOUND
    NAME: ...
    URL: ...
    PWD: ...
"""
import json
import os
import re
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

from playwright.sync_api import sync_playwright

ROOT = os.path.dirname(os.path.abspath(__file__))
SESSION = os.path.join(ROOT, "session", "lanzou-session.json")
PROFILE = os.path.join(ROOT, ".pw-lanzou-share")


def load_cookies():
    with open(SESSION, encoding="utf-8") as f:
        data = json.load(f)
    cookies = data.get("Cookies") or {}
    items = []
    for name, value in cookies.items():
        for domain in (".lanzou.com", ".woozooo.com"):
            items.append({"name": name, "value": value, "domain": domain, "path": "/"})
    return items


def find_file_frame(page, timeout=40):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for f in page.frames:
            if "mydisk.php" in f.url and "item=files" in f.url:
                return f
        time.sleep(2)
    return None


def main():
    file_name = sys.argv[1] if len(sys.argv) > 1 else ""
    if not file_name:
        sys.exit("缺少文件名参数")
    if not os.path.exists(SESSION):
        sys.exit("未找到登录状态")

    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(
            PROFILE, channel="msedge", headless=False,
            viewport={"width": 1280, "height": 900},
        )
        page = context.pages[0] if context.pages else context.new_page()
        context.add_cookies(load_cookies())
        page.goto("https://up.woozooo.com/u", timeout=60000)
        time.sleep(6)
        frame = find_file_frame(page)
        if frame is None:
            sys.exit("找不到文件管理页面")

        deadline = time.time() + 60
        found = False
        while time.time() < deadline:
            try:
                body = frame.inner_text("body")
            except Exception:
                body = ""
            if file_name in body:
                found = True
                break
            time.sleep(3)

        if not found:
            print("FILE_NOT_FOUND")
            context.close()
            return 1
        print("文件已找到")

        # 找到文件所在行，点“分享”
        url, pwd = "", ""
        try:
            # 找到包含文件名的元素，向上找操作按钮所在容器
            node = frame.get_by_text(file_name, exact=False).first
            chain = node.evaluate(
                """el => {
                    const out = [];
                    let cur = el;
                    for (let i = 0; i < 5 && cur; i++) {
                        out.push({tag: cur.tagName, cls: cur.className, id: cur.id,
                                  html: cur.outerHTML.slice(0, 900)});
                        cur = cur.parentElement;
                    }
                    return out;
                }"""
            )
            for c in chain:
                print("CHAIN:", json.dumps(c, ensure_ascii=False)[:1000])
            # 文件名上的 onclick 就是 f_sha(fid)（打开分享弹窗）
            node.evaluate(
                """el => {
                    const link = el.closest('.aspanlink') || el.parentElement;
                    if (link && link.click) link.click();
                }"""
            )
            print("已触发分享")
            time.sleep(4)
        except Exception as e:
            print("SHARE_CLICK_ERR:", e)

        # 提取链接：先找输入框，再全文正则
        body_all = frame.inner_text("body")
        for sel in ("input[readonly]", "input[type=text]", "#share input", ".share input"):
            try:
                loc = frame.locator(sel)
                if loc.count():
                    val = loc.first.input_value()
                    if "lanzou" in val or "woozooo" in val:
                        url = val
                        break
            except Exception:
                pass
        if not url:
            m = re.search(r"https?://[^\s\"'<>]+?(?:lanzou|woozooo)[^\s\"'<>]*", body_all)
            if m:
                url = m.group(0).rstrip(".,;)")
        if not url:
            # 打印弹窗区域文本和所有输入框值，便于继续调试
            try:
                vals = frame.eval_on_selector_all(
                    "input", "els => els.map(e => e.value).filter(v => v && v.length > 5)"
                )
                print("INPUT_VALUES:", json.dumps(vals, ensure_ascii=False)[:600])
                print("BODY_TAIL:", body_all[-600:].replace("\n", " "))
            except Exception:
                pass
        pwd_m = re.search(r"(?:提取码|密码)[:：\s]*([0-9A-Za-z]{2,6})", body_all)
        if pwd_m:
            pwd = pwd_m.group(1)

        if not url:
            print("URL_NOT_FOUND")
            print("BODY:", body_all[:800].replace("\n", " "))
            context.close()
            return 2

        print("OK")
        print("NAME: %s" % file_name)
        print("URL: %s" % url)
        print("PWD: %s" % pwd)
        context.close()
        return 0


if __name__ == "__main__":
    main()
