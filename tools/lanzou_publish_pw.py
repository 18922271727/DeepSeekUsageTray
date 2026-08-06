#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
蓝奏云上传（真实浏览器方案）：用本机 Edge 打开蓝奏云文件管理页，
自动注入文件并上传，上传完成后抓取分享链接。

用法:
    python lanzou_publish_pw.py <文件路径>

输出(供发布脚本解析):
    OK
    NAME: <文件名>
    URL: <分享链接>
    PWD: <提取码(没有则为空)>
    SIZE_MB: <大小>
"""
import argparse
import json
import os
import re
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
    ap = argparse.ArgumentParser(description="蓝奏云上传（浏览器方案）")
    ap.add_argument("file")
    ap.add_argument("--session", default=None)
    ap.add_argument("--headless", action="store_true")
    args = ap.parse_args()

    file_path = os.path.abspath(args.file)
    if not os.path.isfile(file_path):
        sys.exit("文件不存在: " + file_path)

    session_path = args.session or SESSION
    if not os.path.exists(session_path):
        sys.exit("未找到蓝奏云登录状态，请先双击 tools\\lanzou-login.cmd 登录")
    os.environ["DEEPSEEK_LANZOU_SESSION"] = session_path

    file_name = os.path.basename(file_path)
    shot_dir = os.path.join(ROOT, "session", "shots")
    os.makedirs(shot_dir, exist_ok=True)

    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(
            PROFILE,
            channel="msedge",
            headless=args.headless,
            viewport={"width": 1280, "height": 900},
        )
        page = context.pages[0] if context.pages else context.new_page()
        context.add_cookies(load_cookies())

        page.goto("https://up.woozooo.com/u", timeout=60000)
        time.sleep(6)
        page.screenshot(path=os.path.join(shot_dir, "01-console.png"))

        frame = find_file_frame(page)
        if frame is None:
            sys.exit("找不到文件管理页面，请检查登录状态")
        print("FRAME:", frame.url)

        # 注入文件到上传队列
        picker = frame.locator("#filePicker input[type=file]")
        if picker.count() == 0:
            picker = frame.locator("input[type=file]").first
        picker.set_input_files(file_path)
        print("文件已加入上传队列")
        time.sleep(2)
        page.screenshot(path=os.path.join(shot_dir, "02-queued.png"))

        # 点“开始上传”
        upload_btn = frame.locator(".uploadBtn")
        if upload_btn.count() == 0:
            upload_btn = frame.get_by_text("开始上传")
        if upload_btn.count() > 0:
            try:
                upload_btn.first.click(force=True, timeout=8000)
            except Exception:
                upload_btn.first.evaluate("el => el.click()")
            print("已点击开始上传")
        else:
            print("未找到开始上传按钮，尝试自动上传")

        # 等待上传完成：文件出现在列表中且不再有进度
        deadline = time.time() + 600
        done = False
        while time.time() < deadline:
            time.sleep(4)
            list_text = frame.inner_text("body")
            if file_name in list_text:
                # 检查是否仍在队列（上传中）
                queued = ""
                try:
                    if frame.locator("ul.filelist").count():
                        queued = frame.locator("ul.filelist").first.inner_text()
                    elif frame.locator("#filelist").count():
                        queued = frame.locator("#filelist").first.inner_text()
                except Exception:
                    queued = ""
                if "上传中" not in queued and "等待上传" not in queued and "%" not in queued:
                    done = True
                    break
        page.screenshot(path=os.path.join(shot_dir, "03-after-upload.png"))
        if not done:
            sys.exit("上传超时或未完成，请检查浏览器窗口")
        print("上传完成，等待列表刷新")
        time.sleep(5)
        page.screenshot(path=os.path.join(shot_dir, "04-list.png"))

        # 打印文件列表 HTML 片段，便于定位分享按钮
        rows_html = frame.locator("table, .filelist, .mydisk_file_list").count()
        snippet = ""
        for sel in ("table", ".filelist", ".mydisk_file_list"):
            if frame.locator(sel).count():
                snippet = frame.locator(sel).first.inner_html()
                break
        if snippet:
            idx = snippet.find(file_name)
            print("ROW_SNIPPET:", snippet[max(0, idx - 400): idx + 1200] if idx >= 0 else snippet[:1200])

        # 尝试点击该文件行的分享按钮
        share_clicked = False
        try:
            row = frame.locator("tr", has_text=file_name).last
            if row.count():
                share_btns = row.locator("a, button", has_text=re.compile(r"分享|复制"))
                if share_btns.count():
                    share_btns.first.click()
                    share_clicked = True
                    print("已点击分享")
        except Exception as e:
            print("SHARE_CLICK_ERR:", e)

        time.sleep(3)
        page.screenshot(path=os.path.join(shot_dir, "05-share.png"))

        url, pwd = "", ""
        if share_clicked:
            # 在弹窗/页面中找链接输入框
            for sel in ("input[readonly]", "input[type=text]", ".share-url input", "#share input"):
                loc = frame.locator(sel)
                if loc.count():
                    val = loc.first.input_value()
                    if "lanzou" in val or "woozooo" in val:
                        url = val
                        break
            if not url:
                body = frame.inner_text("body")
                m = re.search(r"https?://[^\s\"'<>]+(?:lanzou|woozooo)[^\s\"'<>]*", body)
                if m:
                    url = m.group(0).rstrip(".,;)")
            pwd_m = re.search(r"(?:提取码|密码)[:：\s]*([0-9A-Za-z]{2,6})", frame.inner_text("body"))
            if pwd_m:
                pwd = pwd_m.group(1)

        if not url:
            print("WARN: 未抓取到分享链接，请人工在浏览器里点击分享后复制链接")
            context.close()
            return 2

        print("OK")
        print("NAME: %s" % file_name)
        print("URL: %s" % url)
        print("PWD: %s" % pwd)
        print("SIZE_MB: %.2f" % (os.path.getsize(file_path) / 1048576.0))
        context.close()
        return 0


if __name__ == "__main__":
    main()
