# -*- coding: utf-8 -*-
"""从《白卡植物标签表.xlsx》导出标签数据，并**当场校验**。

产出：`../data/whitecard_tags.json`
  { "groupOrder": [...5 大类...], "groups": {大类: [标签...]}, "plants": {植物键: {name, tags}} }

校验项（任何一项不通过都会打印出来，便于发现表格改坏）：
  · 每株植物都必须有「植物键」
  · 植物标签列里的每个标签都必须能在「标签字典」里找到
  · 分组只保留那 5 个大类（字典里的「备注/脚本特化」命中 0，表格说明已并入「其他」）

用法：
    python tools/extract_whitecard_tags.py
"""
import json
import pathlib
from collections import OrderedDict

from openpyxl import load_workbook

BASE = pathlib.Path(__file__).resolve().parent.parent      # Mod 根目录
XLSX = BASE / "data" / "白卡植物标签表.xlsx"
OUT = BASE / "data" / "whitecard_tags.json"

# 5 大类（表格「标签字典」的分组列）
GROUP_ORDER = ["攻击", "生产", "职能", "系列", "属性"]

# 标签列里可能用到的分隔符（顿号 / 中文逗号 / 英文逗号）
SEPS = "、,，"


def main() -> int:
    if not XLSX.is_file():
        print("找不到表格：", XLSX)
        return 1
    wb = load_workbook(XLSX, data_only=True)

    # ── 1) 标签字典：标签 -> 分组
    ws = wb["标签字典"]
    tag2group = OrderedDict()
    for r in ws.iter_rows(min_row=2, values_only=True):
        if not r or r[0] is None:
            continue
        tag2group[str(r[0]).strip()] = (str(r[1]).strip() if r[1] else "")

    # ── 2) 5 大类 → 标签（丢弃「备注」组）
    groups = OrderedDict((g, []) for g in GROUP_ORDER)
    dropped = []
    for tag, grp in tag2group.items():
        if grp in groups:
            groups[grp].append(tag)
        else:
            dropped.append((tag, grp))

    # ── 3) 白卡植物标签：植物键 -> 标签[]
    ws2 = wb["白卡植物标签"]
    plants = OrderedDict()
    unknown_tags = {}
    bad_rows = []
    for r in ws2.iter_rows(min_row=2, values_only=True):
        if not r or r[0] is None:
            continue
        name = str(r[1]).strip() if r[1] else ""
        key = str(r[2]).strip() if r[2] else ""
        tagstr = str(r[7]).strip() if len(r) > 7 and r[7] else ""
        if not key:
            bad_rows.append(r[:3])
            continue
        tags = []
        for part in tagstr.replace("，", "、").split("、"):
            for t in part.split(","):
                t = t.strip()
                if t and t not in tags:
                    tags.append(t)
        plants[key] = {"name": name, "tags": tags}
        for t in tags:
            if t not in tag2group:
                unknown_tags.setdefault(t, []).append(key)

    # ── 4) 报告
    print("=== 分组统计 ===")
    for g in GROUP_ORDER:
        print("  {}: {} 个标签".format(g, len(groups[g])))
    if dropped:
        print("  丢弃的分组（表格说明已并入「其他」）:", dropped)
    print()
    print("=== 植物数 ===", len(plants))
    print()
    ok = True
    if unknown_tags:
        ok = False
        print("!! 表格里出现但字典里没有的标签:")
        for t, ks in unknown_tags.items():
            print("   {}  例: {}".format(t, ks[:3]))
    else:
        print("OK 所有植物标签都在字典里")
    if bad_rows:
        ok = False
        print("!! 缺植物键的行:", bad_rows)
    else:
        print("OK 所有行都有植物键")
    print()

    out = {"groupOrder": GROUP_ORDER, "groups": groups, "plants": plants}
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    print("已写出:", OUT, OUT.stat().st_size, "B")
    return 0 if ok else 2


if __name__ == "__main__":
    raise SystemExit(main())
