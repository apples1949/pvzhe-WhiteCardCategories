# -*- coding: utf-8 -*-
"""从 `Translate.zh.translation`（Godot OptimizedTranslation / RSCC + zstd 帧）里取出图鉴文案。

结构（实测）：把所有 zstd 帧解开拼起来后，是一串以 \\x00 分隔的文本，
每个图鉴条目按 [图鉴故事][中文名][简介][属性(属性块，含「获取方式」)] 的顺序相邻。
本模块提供 load()：中文名 → {intro, detail, story}。

用法（自检）：
    python tools/zh_texts.py
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
TRANS = os.path.join(BASE, "unpack", "Asset", "Translate", "Translate.zh.translation")

COLOR = re.compile(r"\[/?(?:color|b|i|u|s)(?:=[0-9a-fA-F]+)?\]")


def strip_bb(s):
    """去掉 BBCode 颜色标记（表格口径：只去标记，保留换行）。"""
    return COLOR.sub("", s or "").strip()


def _blob():
    import zstandard
    b = io.open(TRANS, "rb").read()
    start = b.find(b"\x28\xb5\x2f\xfd")          # 第一个 zstd 帧
    if start < 0:
        raise SystemExit("没找到 zstd 帧：" + TRANS)
    out, off = [], start
    while off < len(b) - 4:
        obj = zstandard.ZstdDecompressor().decompressobj()
        try:
            piece = obj.decompress(b[off:])
            if not piece:
                off += 1
                continue
            out.append(piece)
            off = len(b) - len(obj.unused_data)
        except Exception:
            off += 1
    return b"".join(out).decode("utf-8", errors="replace")


def load():
    """中文名 → {intro, detail, story}；detail 形如「获取方式：...\\n\\n韧性：300\\n...」。"""
    fields = _blob().split("\x00")
    idx = {}
    for i, f in enumerate(fields):
        if not f:
            continue
        # 名字后面紧跟「简介」、再后面是含「获取方式」或「韧性」的属性块
        if i + 2 < len(fields):
            nxt, nxt2 = fields[i + 1], fields[i + 2]
            if nxt and nxt2 and ("获取方式" in nxt2 or "韧性" in nxt2):
                if f not in idx:
                    idx[f] = {
                        "intro": strip_bb(nxt),
                        "detail": strip_bb(nxt2),
                        "story": strip_bb(fields[i - 1]) if i > 0 else "",
                    }
    return idx


if __name__ == "__main__":
    d = load()
    print("解析到图鉴条目：", len(d))
    for k in ["魔法睡莲", "寒冰海草", "咖啡路灯花", "豌豆向日葵", "火炬坚果"]:
        v = d.get(k)
        print("---", k, "→", "有" if v else "缺")
        if v:
            print("   简介:", v["intro"][:60])
            print("   属性:", v["detail"].replace("\n", " / ")[:120])
            print("   故事:", v["story"][:60])
