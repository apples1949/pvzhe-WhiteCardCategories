# -*- coding: utf-8 -*-
"""回环校验：生成的 `WhiteCardCategoriesTable.cs` 是否与《白卡植物标签表.xlsx》完全一致。

为什么需要它：烘焙表是**生成**出来的，一旦生成脚本出错（漏行、串行、分隔符处理错），
表现只会是"某株植物少了个标签"，肉眼极难发现。这里逐项比对：
  · 5 大类顺序
  · 每个大类下的标签列表（顺序也比）
  · 每株植物的标签列表（含顺序）
  · 植物标签是否都能落到 5 大类里（无游离标签）

用法：
    python tools/verify_table_roundtrip.py
退出码 0 = 通过。
"""
import json
import pathlib
import re

BASE = pathlib.Path(__file__).resolve().parent.parent      # Mod 根目录
CS = BASE / "runtime_src" / "WhiteCardCategoriesTable.cs"
J = BASE / "data" / "whitecard_tags.json"


def main() -> int:
    if not CS.is_file() or not J.is_file():
        print("缺少文件：", CS if not CS.is_file() else J)
        return 1
    src = CS.read_text(encoding="utf-8")
    data = json.loads(J.read_text(encoding="utf-8"))

    fails = []

    # ── 解析 C# 表
    m = re.search(r"GroupOrder = \{ ([^}]+) \}", src)
    if not m:
        print("!! 解析不出 GroupOrder")
        return 1
    cs_order = re.findall(r'"([^"]+)"', m.group(1))

    cs_groups = {}
    gm = re.search(r"Groups = new Dictionary<string, string\[\]>\s*\{(.*?)\n\t\};", src, re.S)
    if gm:
        for line in gm.group(1).splitlines():
            mm = re.match(r'\s*\{ "([^"]+)", new\[\] \{ (.*) \} \},', line)
            if mm:
                cs_groups[mm.group(1)] = re.findall(r'"([^"]+)"', mm.group(2))

    cs_plants = {}
    plm = re.search(r"PlantTagLines =\s*\{(.*?)\n\t\};", src, re.S)
    if plm:
        for line in plm.group(1).splitlines():
            mm = re.match(r'\s*"([^|]+)\|([^"]*)",', line)
            if mm:
                cs_plants[mm.group(1)] = [t for t in mm.group(2).split(",") if t]

    # ── 比对
    if cs_order != data["groupOrder"]:
        fails.append("大类顺序不一致: C#={} vs 表格={}".format(cs_order, data["groupOrder"]))
    for g, tags in data["groups"].items():
        if g not in cs_groups:
            fails.append("C# 缺少大类: " + g)
        elif cs_groups[g] != tags:
            fails.append("大类 {} 的标签不一致\n   C#={}\n   表格={}".format(g, cs_groups[g], tags))

    if set(cs_plants) != set(data["plants"]):
        only_cs = set(cs_plants) - set(data["plants"])
        only_x = set(data["plants"]) - set(cs_plants)
        fails.append("植物集合不一致: 仅C#={} 仅表格={}".format(sorted(only_cs)[:5], sorted(only_x)[:5]))

    diff = 0
    for k, v in data["plants"].items():
        if cs_plants.get(k) != v["tags"]:
            diff += 1
            if diff <= 5:
                fails.append("植物 {} 标签不一致: C#={} 表格={}".format(k, cs_plants.get(k), v["tags"]))

    all_tags = set()
    for tags in cs_groups.values():
        all_tags |= set(tags)
    stray = set()
    for tags in cs_plants.values():
        for t in tags:
            if t not in all_tags:
                stray.add(t)
    if stray:
        fails.append("植物标签里有不在 5 大类里的: " + str(sorted(stray)))

    # ── 报告
    print("=== 回环校验（C# 表 vs 表格数据）===")
    print("  C# 大类数       :", len(cs_order))
    print("  C# 标签总数     :", sum(len(v) for v in cs_groups.values()))
    print("  C# 植物数       :", len(cs_plants))
    print("  表格植物数      :", len(data["plants"]))
    print("  标签不一致的植物:", diff)
    print()
    if fails:
        print("!! 发现 {} 处问题:".format(len(fails)))
        for f in fails:
            print("   -", f)
        return 1
    print("OK 生成表与表格完全一致（大类/标签/植物标签逐项比对通过）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
