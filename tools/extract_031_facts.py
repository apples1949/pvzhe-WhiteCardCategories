# -*- coding: utf-8 -*-
"""从 0.31 解包提取白卡事实（成本/冷却/韧性/属性旗标/组件/弹道/图鉴文案），供制表与打标签用。

用法：
    python tools/extract_031_facts.py                # 打印统计 + 缺口 14 株的明细
    python tools/extract_031_facts.py --json out.json
"""
import io
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
UNPACK = os.path.join(BASE, "unpack")
PLANT_ROOT = os.path.join(UNPACK, "Asset", "Anime", "Character", "Plant")

# ── physiqueTypeFlags 位值（TowerDefenseEnum.CHARACTER_PHYSIQUE_TYPE）──
PHY = {
    "坚果": 1, "花盆": 2, "莲叶": 4, "咖啡豆": 8, "地刺": 0x10, "花瓶": 0x20,
    "灯具": 0x40, "辣椒": 0x80, "猫尾": 0x100, "不可穿透": 0x400, "机器": 0x800,
    "魔法": 0x1000,
}
EL = {"冰": 1, "火": 2}
# fireMethodFlags（TowerDefenseProjectileCreateData）
FM = {"射击": 1, "投抛": 2, "穿透": 4, "后向弹道": 8, "滚动弹道": 16, "猫尾": 32}

COMP_TAGS = [
    ("ExplodeComponent", ["一次性"]),
    ("AttackComponent", ["近战"]),
    ("MousePressComponent", ["发射"]),
    ("CannonComponent", ["发射"]),
    ("PeriodicAreaEventComponent", ["反击"]),
    ("PotatoComponent", ["地雷"]),
    ("MagnetComponent", ["磁力"]),
    ("BlockComponent", ["保护伞"]),
    ("BloverComponent", ["吹飞"]),
    ("SquashComponent", ["窝瓜"]),
    ("GrowUpComponent", ["成长"]),
    ("TanglekelpComponent", ["缠绕"]),
    ("ChomperComponent", ["大嘴花"]),
    ("GravebusterComponent", ["墓吞"]),
    ("ScaredComponent", ["胆小"]),
    ("SlotComponent", ["花盆"]),
    ("BowlingComponent", ["滚动攻击"]),
    ("MagnetCoinComponent", ["吸金", "产资源"]),
]

SUN_PRODUCE = {"sun", "brainsun", "jalasun", "qx sun", "qxsun", "magicsun"}

SERIES_KW = [
    ("豌豆", ["pea", "peater", "gatling", "kabbage"]),
    ("向日葵", ["sunflower", "sunfl"]),
    ("寒冰", ["snow"]),
    ("大蒜", ["garlic"]),
    ("樱桃", ["cherry"]),
    ("玉米", ["corn"]),
    ("南瓜壳", ["pumpkin"]),
    ("杨桃", ["star"]),
    ("僵尸", ["zombie"]),
    ("灯具", ["plantern", "lantern", "lamp", "light"]),
    ("土豆雷", ["potatomine", "wildmine", "magnetmine", "cherrymine"]),
    ("三叶草", ["blover"]),
    ("喷菇", ["fume", "shroom"]),
    ("树桩", ["wood"]),
    ("毁灭", ["doom", "taboo"]),
    ("全息", ["hologram", "holograph"]),
    ("仙人掌", ["cactus"]),
    ("忧郁", ["gloom"]),
    ("促销", ["sale"]),
    ("高坚果", ["tallnut"]),
    ("小喷菇", ["puff"]),
    ("裂荚", ["split"]),
    ("西瓜", ["melon"]),
    ("模仿者", ["imitater"]),
    ("咖啡豆", ["coffee"]),
    ("辣椒", ["jala"]),
    ("魔法", ["magic"]),
    ("海兵菇", ["sealeaf", "iceseashroom"]),
]


def read(p):
    return io.open(p, encoding="utf-8", errors="replace").read()


def parse_fields(text):
    """把 tres 的顶层 key = value 行解析成 dict（只取 [resource] 段）。"""
    out = {}
    body = text.split("[resource]", 1)
    body = body[1] if len(body) > 1 else text
    for m in re.finditer(r"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$", body, re.M):
        out[m.group(1)] = m.group(2).strip()
    return out


def load_translate():
    tr = {}
    csvp = os.path.join(UNPACK, "Asset", "Translate", "Translate.csv")
    for ln in read(csvp).split("\n")[1:]:
        p = ln.split(",")
        if len(p) >= 4:
            tr[p[0].strip()] = p[-1].strip()
    return tr


def load_zh_raw():
    p = os.path.join(UNPACK, "Asset", "Translate", "Translate.zh.translation")
    if not os.path.exists(p):
        return ""
    return io.open(p, "rb").read().decode("utf-8", errors="replace")


def zh_text(raw, key):
    """取 Translate.zh.translation 里 key 对应的译文（文本以 \\x00 分隔）。"""
    if not raw or not key:
        return ""
    m = re.search(re.escape("\x00" + key + "\x00"), raw)
    if not m:
        return ""
    parts = raw[m.end():].split("\x00")
    return parts[0].strip() if parts else ""


def white_list():
    p = os.path.join(UNPACK, "Asset", "Config", "PacketBank", "PacketBankResource.json")
    data = json.load(io.open(p, encoding="utf-8"))

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
        return [str(x) for x in w]
    return []


def packet_index():
    """saveKey -> packet tres 路径 / 配置 tres 路径。"""
    idx = {}
    for dp, dn, fn in os.walk(PLANT_ROOT):
        for f in fn:
            if not f.endswith(".tres"):
                continue
            p = os.path.join(dp, f)
            t = read(p)
            if 'script_class="TowerDefensePacketConfig"' not in t:
                continue
            m = re.search(r'^saveKey = "(Plant[A-Za-z0-9_]+)"', t, re.M)
            if not m:
                continue
            cfg = re.search(r'path="res://(Asset/Anime/Character/Plant/[^"]*?TowerDefensePlant[A-Za-z0-9_]+\.tres)"', t)
            cfgp = os.path.join(UNPACK, cfg.group(1).replace("/", os.sep)) if cfg else ""
            idx[m.group(1)] = {"packet": p, "packet_fields": parse_fields(t), "config": cfgp}
    return idx


def flatten_components(set_path, seen=None):
    """读 CharacterComponentSet：ParentSet 递归 + Components 里每个子资源的 ComponentTypeId。"""
    if seen is None:
        seen = set()
    ids, produce, projectiles = [], "", []
    if not set_path or not os.path.exists(set_path) or set_path in seen:
        return ids, produce, projectiles
    seen.add(set_path)
    t = read(set_path)
    ext = {}
    for m in re.finditer(r'^\[ext_resource[^\]]*path="res://([^"]+)"[^\]]*id="([^"]+)"', t, re.M):
        ext[m.group(2)] = os.path.join(UNPACK, m.group(1).replace("/", os.sep))
    parent = re.search(r'^ParentSet = ExtResource\("([^"]+)"\)', t, re.M)
    if parent and parent.group(1) in ext:
        i2, p2, pj2 = flatten_components(ext[parent.group(1)], seen)
        ids += i2
        produce = produce or p2
        projectiles += pj2
    # 子资源块
    blocks = re.split(r"^\[sub_resource", t, flags=re.M)[1:]
    for b in blocks:
        cid = re.search(r'^ComponentTypeId = "([^"]+)"', b, re.M)
        if cid:
            ids.append(cid.group(1))
            pt = re.search(r'^produceType = "?([^"\n]+)"?', b, re.M)
            if cid.group(1) == "ProduceComponent" and pt:
                produce = pt.group(1).strip().lower()
        pn = re.search(r'^projectileName = &"([^"]+)"', b, re.M)
        if pn:
            projectiles.append({
                "name": pn.group(1),
                "fireMethodFlags": (re.search(r'^fireMethodFlags = (\d+)', b, re.M) or [None, ""])[1],
                "penetrateNum": (re.search(r'^penetrateNum = (-?\d+)', b, re.M) or [None, ""])[1],
                "catapultHeight": (re.search(r'^catapultHeight = ([\d.]+)', b, re.M) or [None, ""])[1],
            })
    # ext_resource 里直接给的组件定义（如 SlotComponent/LilyPadMagicSlotComponentDefinition.tres）
    comps = re.search(r'^Components = \[(.*)\]', t, re.M)
    if comps:
        for eid in re.findall(r'ExtResource\("([^"]+)"\)', comps.group(1)):
            p = ext.get(eid)
            if p and os.path.exists(p):
                t2 = read(p)
                c2 = re.search(r'^ComponentTypeId = "([^"]+)"', t2, re.M)
                if c2:
                    ids.append(c2.group(1))
    return ids, produce, projectiles


def facts_of(key, idx, tr, raw):
    ent = idx.get(key)
    if not ent:
        return None
    pf = ent["packet_fields"]
    name_key = pf.get("name", "").strip('"')
    f = {
        "key": key,
        "cn": tr.get(name_key, ""),
        "intro": zh_text(raw, pf.get("describe", "").strip('"')),
        "handbook": zh_text(raw, pf.get("handbookDescribe", "").strip('"')),
        "story": zh_text(raw, pf.get("handbookStory", "").strip('"')),
        "config": ent["config"],
    }
    if ent["config"] and os.path.exists(ent["config"]):
        cf = parse_fields(read(ent["config"]))
        f["cost"] = cf.get("cost", "")
        f["cooldown"] = cf.get("packetCooldown", "")
        f["hitpoints"] = cf.get("hitpoints", "")
        f["physique"] = cf.get("physiqueTypeFlags", "")
        f["element"] = cf.get("elementFlags", "")
        f["grid"] = cf.get("plantGridType", "")
        f["sleep"] = cf.get("sleepTime", "")
        f["cover"] = cf.get("plantCover", "")
        base = os.path.basename(ent["config"]).replace(".tres", "")
        setp = os.path.join(os.path.dirname(os.path.dirname(ent["config"])), "Scene", base.replace("TowerDefensePlant", "TowerDefensePlant") + "ComponentSet.tres")
        if not os.path.exists(setp):
            cands = [x for x in os.listdir(os.path.dirname(setp)) if x.endswith("ComponentSet.tres")] if os.path.isdir(os.path.dirname(setp)) else []
            setp = os.path.join(os.path.dirname(setp), cands[0]) if len(cands) == 1 else ""
        f["componentset"] = setp
        ids, produce, projectiles = flatten_components(setp)
        f["components"] = sorted(set(ids))
        f["produce"] = produce
        f["projectiles"] = projectiles
    return f


def tag(f):
    """按《标签字典》规则打标签（只做能自动判定的部分）。"""
    t = []
    lk = f["key"].lower()

    def add(x):
        if x and x not in t:
            t.append(x)

    phy = int(f.get("physique", "0") or 0)
    el = int(f.get("element", "0") or 0)
    for name, bit in PHY.items():
        if phy & bit:
            add(name)
    for name, bit in EL.items():
        if el & bit:
            add(name)
    if (f.get("sleep") or "").strip('"').lower() == "day":
        add("夜行")
    grid = f.get("grid") or ""
    gnums = [int(x) for x in re.findall(r"\d+", grid)]
    if 3 in gnums:
        add("水生" if gnums == [3] else "水陆")
    if (f.get("cover") or "").strip() not in ("", "[]", '""'):
        add("仅底座")
    try:
        if float(f.get("hitpoints") or 0) >= 3000:
            add("坚果")
    except ValueError:
        pass

    comps = f.get("components") or []
    produce = f.get("produce") or ""
    for cid, tags in COMP_TAGS:
        if cid in comps:
            for x in tags:
                if x == "产资源" and cid == "MagnetCoinComponent":
                    add(x)
                elif x != "产资源":
                    add(x)
    if any(c.startswith("ChangeProjectile") for c in comps):
        add("火炬")
    if "ProduceComponent" in comps and produce:
        add("产阳光" if produce in SUN_PRODUCE else "产资源")

    for pj in f.get("projectiles") or []:
        fm = int(pj.get("fireMethodFlags") or 1)
        for name, bit in FM.items():
            if fm & bit:
                add(name)
        pn = int(pj.get("penetrateNum") or 0)
        if pn > 1 or pn == -1:
            add("穿透")

    for name, kws in SERIES_KW:
        if any(w in lk for w in kws):
            add(name)
    if lk.endswith("z"):
        add("僵尸")
    return t


def main():
    tr = load_translate()
    raw = load_zh_raw()
    idx = packet_index()
    white = white_list()
    print("White 白卡：%d 株，packet 索引：%d" % (len(white), len(idx)))
    allf = {}
    for k in white:
        f = facts_of(k, idx, tr, raw)
        if f:
            allf[k] = f
    print("成功提取：%d 株" % len(allf))
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        io.open(out, "w", encoding="utf-8").write(json.dumps(allf, ensure_ascii=False, indent=1))
        print("写出:", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
