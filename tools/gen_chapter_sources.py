# -*- coding: utf-8 -*-
"""生成 WhiteCardChapterSources.cs：白卡 → 章节（来源分组）映射。

数据源（两路合并）：
  1) **xlsx** `白卡植物标签表.xlsx` 的「图鉴说明」列「获取方式」文本 —— 188 株，权威。
  2) **游戏解包** `unpack/` —— 用于补齐 xlsx 之后新版本新增的白卡：
     - 白卡名单：`Asset/Config/PacketBank/PacketBankResource.json` → GeneralPlant.Category.White
     - 章节依据：优先用 `Translate.zh.translation` 图鉴条目的「获取方式」文本（与 xlsx 同口径），
                 缺失时回退到 packet .tres 的 `levelSaveKey`
     - 中文名：`Asset/Translate/Translate.csv`

用法：python _gen_chapter_sources.py
"""
import io, os, re, json, sys
from collections import OrderedDict
import openpyxl

sys.stdout.reconfigure(encoding="utf-8")

BASE = r"C:\Users\apples1949\Desktop\植物大战僵尸杂交重制版"
MOD = os.path.join(BASE, "mods", "WhiteCardCategories")
XLSX = os.path.join(BASE, "白卡植物标签表.xlsx")
UNPACK = os.path.join(BASE, "unpack")

wb = openpyxl.load_workbook(XLSX, data_only=True)

# ── 1. 读 xlsx「图鉴说明」→ 植物键 → 获取方式 ──
ws = wb["图鉴说明"]
acq = OrderedDict()
order = []
for r in ws.iter_rows(min_row=2, values_only=True):
    if not r or r[0] is None:
        continue
    idx, name, key, intro, detail, story = (list(r) + [None] * 6)[:6]
    if not key:
        continue
    detail = detail or ""
    m = re.search(r"获取方式[：:]\s*([^\n\r]*)", detail)
    acq[key] = (name, m.group(1).strip() if m else "")
    order.append(key)

xlsx_keys = set(order)

# ── 2. 读游戏解包，补齐 xlsx 之外的白卡 ──
_uni = {}


def load_universe():
    """返回 OrderedDict(key -> (中文名, 获取方式文本, levelSaveKey))，顺序 = White 数组顺序。"""
    if _uni:
        return _uni
    # 2.1 中文名表
    tr = {}
    csvp = os.path.join(UNPACK, "Asset", "Translate", "Translate.csv")
    if os.path.exists(csvp):
        for ln in io.open(csvp, encoding="utf-8", errors="replace").read().split("\n")[1:]:
            p = ln.split(",")
            if len(p) >= 4:
                tr[p[0].strip()] = p[-1].strip()
    # 2.2 白卡名单（保持顺序）
    white = []
    pbp = os.path.join(UNPACK, "Asset", "Config", "PacketBank", "PacketBankResource.json")
    if os.path.exists(pbp):
        data = json.load(io.open(pbp, encoding="utf-8"))

        def walk(o):
            if isinstance(o, dict):
                for k, v in o.items():
                    if k == "White" and isinstance(v, list) and len(v) > 50:
                        yield v
                    yield from walk(v)
            elif isinstance(o, list):
                for v in o:
                    yield from walk(v)

        for w in walk(data):
            white = [str(x) for x in w]
            break
    # 2.3 图鉴「获取方式」（按 NUL 字段边界精确切块）
    raw = ""
    tranp = os.path.join(UNPACK, "Asset", "Translate", "Translate.zh.translation")
    if os.path.exists(tranp):
        raw = open(tranp, "rb").read().decode("utf-8", errors="replace")

    def acq_of(cn):
        if not cn or not raw:
            return ""
        m = re.search(re.escape("\x00" + cn + "\x00"), raw)
        if not m:
            return ""
        parts = raw[m.end():].split("\x00")
        if len(parts) < 2:
            return ""
        a = re.search(r"获取方式\[/color\]：\[color=[0-9a-fA-F]+\]([^\[]+)\[/color\]", parts[1])
        return a.group(1).strip() if a else ""

    # 2.4 packet .tres：中文名 + levelSaveKey
    meta = {}
    root = os.path.join(UNPACK, "Asset", "Anime", "Character", "Plant")
    if os.path.isdir(root):
        for dp, dn, fn in os.walk(root):
            for f in fn:
                if not f.endswith(".tres"):
                    continue
                p = os.path.join(dp, f)
                t = io.open(p, encoding="utf-8", errors="replace").read()
                if 'script_class="TowerDefensePacketConfig"' not in t:
                    continue
                mk = re.search(r'^saveKey = "(Plant[A-Za-z0-9_]+)"', t, re.M)
                if not mk:
                    continue
                mn = re.search(r'^name = "(.*?)"', t, re.M)
                ml = re.search(r'^levelSaveKey = "(.*?)"', t, re.M)
                cn = tr.get(mn.group(1), "") if mn else ""
                meta[mk.group(1)] = (cn, ml.group(1) if ml else "")

    if white:
        seq = list(white)                      # 白卡名单 = 唯一口径（198 株）
    else:
        seq = list(meta.keys())                # 极端兜底：读不到 PacketBankResource.json 时才用全部 packet
    out = OrderedDict()
    for k in seq:
        cn, lv = meta.get(k, ("", ""))
        out[k] = (cn, acq_of(cn), lv)
    _uni.update(out)
    return _uni


# ── 3. 组定义 ──
GROUPS = OrderedDict([
    ("A1",  ("第1章", 10)),
    ("A2",  ("第2章", 20)),
    ("A3",  ("第3章", 30)),
    ("A4",  ("第4章", 40)),
    ("A5",  ("第5章", 50)),
    ("A6",  ("第6章", 60)),
    ("A7",  ("第7章", 70)),
    ("A8",  ("第8章", 80)),
    ("A9",  ("第9章", 90)),
    ("A10", ("第10章", 100)),
    ("HY",  ("杂交乐园", 200)),
    ("ON",  ("在线关卡", 300)),
    ("SH",  ("商店购买", 400)),
    ("LT",  ("抽奖盒子", 500)),
    ("MINI", ("迷你游戏", 550)),
    ("CHAL", ("挑战模式", 560)),
    ("IZM", ("我是僵尸", 570)),
    ("VS",  ("对战模式", 580)),
    ("EVENT", ("限时活动", 590)),
    ("BA",  ("初始可用", 600)),
])


def classify(text):
    """获取方式文本 → 组编码"""
    if not text:
        return "BA"
    m = re.match(r"通关冒险模式\s*(\d+)\s*[-－]\s*(\d+)", text)
    if m:
        ch = int(m.group(1))
        return "A%d" % ch if 1 <= ch <= 10 else "BA"
    if "杂交乐园" in text:
        return "HY"
    if text.startswith("在线关卡奖励"):
        return "ON"
    if text.startswith("商店购买"):
        return "SH"
    if "抽奖盒子" in text:
        return "LT"
    if "迷你游戏" in text or "砸罐子" in text or "幸运四叶草" in text or "坚果保龄球" in text:
        return "MINI"
    if "挑战模式" in text:
        return "CHAL"
    if "我是僵尸" in text:
        return "IZM"
    if "对战" in text:
        return "VS"
    if "每日挑战" in text or "活动" in text:
        return "EVENT"
    return "BA"


def classify_tres(level_key):
    """packet levelSaveKey → 组编码（获取方式缺失时的回退）"""
    if not level_key:
        return ""
    m = re.match(r"Level(\d+)_", level_key)
    if m:
        ch = int(m.group(1))
        return "A%d" % ch if 1 <= ch <= 10 else ""
    if level_key.startswith("HybridPark"):
        return "HY"
    return ""


rows = []          # (key, group, name, rawtext)
counts = OrderedDict((g, 0) for g in GROUPS)
unknown = []

for key in order:                       # ① xlsx 权威部分（顺序 = 表格顺序）
    name, text = acq[key]
    g = classify(text)
    rows.append((key, g, name, text))
    counts[g] += 1
    if g == "BA" and text and not text.startswith("通关冒险模式"):
        unknown.append((key, name, text))

# ② 解包补齐：游戏里有、xlsx 里没有的白卡
extra = []
for k, (cn, text, lv) in load_universe().items():
    if k in xlsx_keys:
        continue
    g = classify(text) or classify_tres(lv)
    if not g:
        g = "BA"
    extra.append((k, g, cn, text or lv))
    counts[g] += 1
    if g == "BA" and text:
        unknown.append((k, cn, text))

extra.sort(key=lambda r: (GROUPS[r[1]][1], r[0]))
rows.extend(extra)

print("xlsx %d 株 + 解包新增 %d 株 = %d 株" % (len(order), len(extra), len(rows)))
print("\n=== 解包新增的白卡 ===")
for k, g, cn, txt in extra:
    print("  %-24s %-5s %-8s %s" % (k, g, cn, txt))

print("\n=== 章节清单 ===")
for g, (label, w) in GROUPS.items():
    if counts[g]:
        print("  %-5s %-8s %3d" % (g, label, counts[g]))
print("  合计 %d 株" % sum(counts.values()))
if unknown:
    print("\n落 BA 且原文不是冒险通关的（需人工确认）：")
    for x in unknown:
        print("   ", x)

# ── 4. 生成 C# ──
L = []
L.append("// <auto-generated> 由 `_gen_chapter_sources.py` 生成，勿手改。")
L.append("//")
L.append("// 数据源：①「白卡植物标签表.xlsx」的「图鉴说明」列「获取方式」文本（权威）；")
L.append("//         ② 游戏解包数据，用于补齐 xlsx 之后新版本新增的白卡（按图鉴获取方式，")
L.append("//            缺失时回退 packet 的 levelSaveKey）。")
L.append("//")
L.append("// 分组来源（共 %d 株）：" % sum(counts.values()))
for g, (label, w) in GROUPS.items():
    if counts[g]:
        L.append("//   %-6s %-8s %3d 株" % (g, label, counts[g]))
L.append("using System.Collections.Generic;")
L.append("")
L.append("internal static class WhiteCardChapterSources")
L.append("{")
L.append("\t/// <summary>章节（来源分组）定义：编码 / 显示名 / 排序权重。顺序即显示顺序。</summary>")
L.append("\tinternal static readonly string[] ChapterGroupLines =")
L.append("\t{")
for g, (label, w) in GROUPS.items():
    if counts[g]:
        L.append('\t\t"%s|%s|%d",' % (g, label, w))
L.append("\t};")
L.append("")
L.append("\t/// <summary>植物键 → 章节编码。编码 \"键|组\"。每株白卡植物恰好一条。</summary>")
L.append("\tinternal static readonly string[] PlantChapterLines =")
L.append("\t{")
for key, g, name, text in rows:
    L.append('\t\t"%s|%s",%s// %s' % (key, g, " " * (2 if len(key) < 26 else 0), name))
L.append("\t};")
L.append("")
L.append("\tprivate static Dictionary<string, string> _byKey;")
L.append("\tprivate static Dictionary<string, string> _labels;")
L.append("\tprivate static string[] _codes;")
L.append("")
L.append("\t/// <summary>按显示顺序排列的所有章节编码。</summary>")
L.append("\tinternal static string[] OrderedCodes")
L.append("\t{")
L.append("\t\tget")
L.append("\t\t{")
L.append("\t\t\tstring[] c = _codes;")
L.append("\t\t\tif (c == null)")
L.append("\t\t\t{")
L.append("\t\t\t\tvar list = new List<string>(ChapterGroupLines.Length);")
L.append("\t\t\t\tforeach (string line in ChapterGroupLines)")
L.append("\t\t\t\t{")
L.append("\t\t\t\t\tint i = line.IndexOf('|');")
L.append("\t\t\t\t\tif (i > 0) { list.Add(line.Substring(0, i)); }")
L.append("\t\t\t\t}")
L.append("\t\t\t\tc = list.ToArray();")
L.append("\t\t\t\t_codes = c;")
L.append("\t\t\t}")
L.append("\t\t\treturn c;")
L.append("\t\t}")
L.append("\t}")
L.append("")
L.append("\t/// <summary>章节编码 → 显示名；查不到返回编码本身。</summary>")
L.append("\tinternal static string LabelOf(string code)")
L.append("\t{")
L.append("\t\tif (string.IsNullOrEmpty(code)) { return \"\"; }")
L.append("\t\tDictionary<string, string> m = _labels;")
L.append("\t\tif (m == null)")
L.append("\t\t{")
L.append("\t\t\tm = new Dictionary<string, string>(ChapterGroupLines.Length);")
L.append("\t\t\tforeach (string line in ChapterGroupLines)")
L.append("\t\t\t{")
L.append("\t\t\t\tstring[] p = line.Split('|');")
L.append("\t\t\t\tif (p.Length >= 2 && p[0].Length > 0) { m[p[0]] = p[1]; }")
L.append("\t\t\t}")
L.append("\t\t\t_labels = m;")
L.append("\t\t}")
L.append("\t\treturn m.TryGetValue(code, out string label) ? label : code;")
L.append("\t}")
L.append("")
L.append("\t/// <summary>查植物所属章节编码；查不到返回 \"\"。</summary>")
L.append("\tinternal static string ChapterOf(string plantKey)")
L.append("\t{")
L.append("\t\tif (string.IsNullOrEmpty(plantKey)) { return \"\"; }")
L.append("\t\tDictionary<string, string> map = _byKey;")
L.append("\t\tif (map == null)")
L.append("\t\t{")
L.append("\t\t\tmap = new Dictionary<string, string>(PlantChapterLines.Length);")
L.append("\t\t\tforeach (string line in PlantChapterLines)")
L.append("\t\t\t{")
L.append("\t\t\t\tint i = line.IndexOf('|');")
L.append("\t\t\t\tif (i <= 0) { continue; }")
L.append("\t\t\t\tmap[line.Substring(0, i)] = line.Substring(i + 1);")
L.append("\t\t\t}")
L.append("\t\t\t_byKey = map;")
L.append("\t\t}")
L.append("\t\treturn map.TryGetValue(plantKey, out string g) ? g : \"\";")
L.append("\t}")
L.append("}")
L.append("")

outp = os.path.join(MOD, "runtime_src", "WhiteCardChapterSources.cs")
# ⚠️ 必须 newline=""：文本模式若同时给 newline="\r\n"，写入的 "\r\n" 会被再翻一次 ⇒ 变成 "\r\n\r\n"（隔行空行）
io.open(outp, "w", encoding="utf-8", newline="").write("\r\n".join(L))
print("\nwrote", outp)
