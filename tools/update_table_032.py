# -*- coding: utf-8 -*-
"""按 0.32 解包更新《白卡植物标签表.xlsx》。

做六件事：
  1. 「白卡植物标签」：追加 0.32 新增的 9 株白卡（表尾序号 203~211），逐株写标签 + 源码依据；
  2. 「标签筛选」：同步追加这 9 株的逐标签行（标签按码位升序，与既有行同序）；
  3. 「图鉴说明」：从 Translate.zh.translation 抽 简介 / 属性（含获取方式）/ 图鉴故事 写入；
     并修正 0.31 那轮补的 14 行「图鉴故事」错位（旧工具把上一株的故事写了进去）；
  4. 「按章节分类」：整表重建（新增分组「内鬼盒子无尽版」）；
  5. 「标签字典」：重算「命中植物数」，并给 0.32 新增判定口径的标签补说明；
  6. 「数据说明」：版本/总数/本次更新 + 新增一节「0.32 新增白卡（9 株）：名单 + 标签 + 依据」。
另外给缺超链接的 23 行（0.31 的 14 株 + 0.32 的 9 株）补上三表互跳超链接。

数据全部来自解包（配置字段 + 组件集 + 弹道资源 + 图鉴文案），标签口径沿用「标签字典」。

用法：
    python tools/update_table_032.py --dry     # 只算不写
    python tools/update_table_032.py           # 写：根目录 xlsx + 同步 data/xlsx
"""
import copy
import io
import json
import os
import re
import shutil
import sys

import openpyxl
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.worksheet.hyperlink import Hyperlink

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)
ROOT = os.path.dirname(os.path.dirname(MOD))
sys.path.insert(0, HERE)

import extract_031_facts as F          # noqa: E402
import zh_texts as Z                   # noqa: E402

XLSX_ROOT = os.path.join(ROOT, "白卡植物标签表.xlsx")
XLSX_DATA = os.path.join(MOD, "data", "白卡植物标签表.xlsx")
BAK_ROOT = os.path.join(ROOT, "白卡植物标签表.bak-0.32前.xlsx")
BAK_DATA = os.path.join(MOD, "data", "白卡植物标签表.bak-0.32前.xlsx")

# ── 章节分组（与 gen_chapter_sources.py / WhiteCardChapterSources.cs 同口径）──
GROUPS = [
    ("A1", "第1章", 10), ("A2", "第2章", 20), ("A3", "第3章", 30), ("A4", "第4章", 40),
    ("A5", "第5章", 50), ("A6", "第6章", 60), ("A7", "第7章", 70), ("A8", "第8章", 80),
    ("A9", "第9章", 90), ("A10", "第10章", 100),
    ("HY", "杂交乐园", 200), ("ON", "在线关卡", 300), ("SH", "商店购买", 400),
    ("LT", "抽奖盒子", 500), ("MINI", "迷你游戏", 550), ("CHAL", "挑战模式", 560),
    ("IZM", "我是僵尸", 570), ("VS", "对战模式", 580),
    ("SPY", "内鬼盒子无尽版", 585),            # ← 0.32 新增模式
    ("EVENT", "限时活动", 590), ("BA", "初始可用", 600),
]
GROUP_LABEL = {g: l for g, l, _ in GROUPS}
GROUP_WEIGHT = {g: w for g, _, w in GROUPS}


def classify(text):
    if not text:
        return "BA"
    m = re.match(r"通关冒险模式\s*(\d+)\s*[-－]\s*(\d+)", text)
    if m:
        ch = int(m.group(1))
        return "A%d" % ch if 1 <= ch <= 10 else "BA"
    if "杂交乐园" in text:
        return "HY"
    if "内鬼盒子" in text:
        return "SPY"
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


# ── 0.32 新增 9 株：攻击/弹道 + 标签 + 源码依据（逐株核定，见各条注释）──
NEW_ROWS = {
    "PlantMelonPumpkin": dict(
        cn="西瓜棚", attack="投抛",
        tags=["投抛", "发射", "坚果", "南瓜壳", "西瓜"],
        evidence=("组件：CharacterTimerComponent（Grow 10s/Check 0.25s）、MousePressComponent；"
                  "physiqueTypeFlags=1（坚果位，韧性 4000）；plantGridType=[7]（与全部南瓜壳系一致）；"
                  "脚本 TowerDefensePlantMelonPumpkin.cs 内 TowerDefenseProjectileCreateData"
                  "{projectileName=Melon, fireMethodFlags=2(CATAPULT), catapultHeight=400} ⇒ 投抛；"
                  "图鉴「特性：南瓜壳」「③可以手动点击成熟的西瓜棚，提前触发攻击」")),
    "PlantIceSunFlower": dict(
        cn="寒冰向日葵", attack="—",
        tags=["产阳光", "寒冰", "向日葵", "冰"],
        evidence=("组件：ProduceComponent（TowerDefensePlantIceSunFlowerProduceComponentDefinition.tres："
                  "produceType=IceSun、num=50、initialDelayRange=[25,25]）⇒ 产阳光；"
                  "elementFlags=1（冰）；配置无 hitpoints 字段（韧性按默认 300）；无 sleepTime（不夜行）；"
                  "图鉴「特性：冰属性」「生产：50阳光/25s」")),
    "PlantDoomPlantern": dict(
        cn="路灯毁灭菇", attack="—",
        tags=["一次性", "灯具", "毁灭", "夜行"],
        evidence=("组件：ExplodeComponent（TowerDefensePlantDoomPlanternExplodeDefinition.tres："
                  "explodeEvent=TowerDefenseCharacterEventExplodeHurt、craterCreateUse=true、explodeRange=3.5）⇒ 一次性；"
                  "sleepTime=Day ⇒ 夜行；脚本调用 GetScreenEffectFeature().AddDoomPlanternLight() 实现路灯花照明"
                  "（含 LanternGlow 节点）⇒ 灯具；名称含「毁灭」")),
    "PlantDoomSunFlower": dict(
        cn="毁灭向日葵", attack="—",
        tags=["一次性", "产阳光", "毁灭", "向日葵", "夜行"],
        evidence=("组件：ProduceComponent（produceType=DoomSun、num=25，叠种 +1 层后 75）⇒ 产阳光"
                  "（DoomSun 在 ProduceComponent.cs 里走 SunDropKind.DoomSun，属 TowerDefenseSunBase 阳光类）；"
                  "ExplodeComponent（DestroySet() 里 TriggerDeathExplosion，伤害 500/1800）⇒ 一次性；"
                  "sleepTime=Day ⇒ 夜行；plantCoverSelf=true（可叠种自身）；名称含「毁灭」「向日葵」")),
    "PlantCoffeeKelp": dict(
        cn="海草咖啡豆", attack="—",
        tags=["缠绕", "咖啡豆"],
        evidence=("physiqueTypeFlags=8（咖啡豆位）、plantGridType=[4]（与阳光豆/咖啡伞等全部咖啡豆系一致）；"
                  "脚本 TowerDefensePlantCoffeeKelp.cs 用 HitBoxSuppressionReason.Tanglekelp 把僵尸拖入水中后双方销毁"
                  "（本体无 TanglekelpComponent）⇒ 缠绕按脚本行为判定；图鉴「缠住靠近的空中僵尸」「特性：咖啡豆」")),
    "PlantFrostShroom": dict(
        cn="霜冻大喷菇", attack="—",
        tags=["近战", "喷菇", "冰", "夜行"],
        evidence=("组件：AttackComponent（checkLine=true、checkShape 前方 6 格、"
                  "eventList=TowerDefenseCharacterEventFrost）⇒ 按近战口径（喷菇系无子弹）；"
                  "elementFlags=1（冰）；sleepTime=Day ⇒ 夜行；名称含「喷菇」；无 FireComponent（攻击列记「—」）")),
    "PlantSquashBomb": dict(
        cn="窝瓜炸弹", attack="—",
        tags=["一次性", "窝瓜"],
        evidence=("组件：ExplodeComponent（TowerDefensePlantSquashBombExplodeDefinition.tres："
                  "explodeShape 240×294、explodeEvent=TowerDefenseCharacterEventExplodeHurt）⇒ 一次性；"
                  "脚本 Explode() 用 GetPacketConfig(\"PlantSquash\") 在 3×3 每格 CanPacketPlant 时种出窝瓜 ⇒ 窝瓜按脚本行为判定"
                  "（本体无 SquashComponent）")),
    "PlantCabbageLeaf": dict(
        cn="叶子卷心菜投手", attack="投抛",
        tags=["投抛", "击退", "保护伞"],
        evidence=("组件：FireComponent（弹道 CabbageLeaf：baseDamage=60、fireMethodFlags=2(CATAPULT)、"
                  "catapultHeight=400、fireInterval=3）⇒ 投抛；BlockComponent（checkShape 73×33、"
                  "catapultBlockRemovesProjectile=true）⇒ 保护伞；弹道资源 Registry/Projectile/Config/Cabbage/CabbageLeaf.tres 的 "
                  "hitTargetEventList 含 TowerDefenseCharacterEventHitBack(length=0.1) ⇒ 击退（图鉴「造成0.1格击退」）")),
    "PlantPresentNG": dict(
        cn="内鬼盒子", attack="—",
        tags=["一次性"],
        evidence=("组件：ExplodeComponent（TowerDefensePlantPresentNGExplodeDefinition.tres：explodeUse=false、"
                  "ImitaterCloud 特效，OnExplode 时抽奖）⇒ 一次性；脚本按 GD.Randi()%4 抽 4 种结果"
                  "（随机植物／被魅惑随机植物／随机非BOSS僵尸／被魅惑随机非BOSS僵尸），图鉴「特性：随机礼盒」；"
                  "plantGridType=[-1]（不限格）；「模仿者」为用户指定（图鉴/脚本无「模仿」字样，行为＝随机礼盒）")),
}


# ── 用户明确指定的追加标签（对「已在表里」的植物做增量补标，幂等）──
# 用途：用户看过表之后点名要加的标签。与 NEW_ROWS 分开，避免重跑脚本时把人工决定冲掉。
USER_PATCHES = {
    "PlantPresentNG": dict(          # 内鬼盒子（0.32 新增第 9 株，序号 211）
        add_tags=["模仿者"],
        replace=[("无模仿行为，故不打「模仿者」",
                  "「模仿者」为用户指定：脚本复用抽奖盒子 TowerDefensePlantPresentBox 的替换管线"
                  "（GetReplacementPlantBank / CreateReplacementPacket / SpawnReplacement），"
                  "开盒后随机变成植物或被魅惑僵尸，行为口径同抽奖盒子")],
        dict_note="内鬼盒子为 0.32 用户指定（图鉴/脚本无「模仿」字样，行为＝随机礼盒抽 4 种结果）",
        desc="用户指定：内鬼盒子+模仿者（0.32，图鉴无「模仿」字样，行为为随机礼盒）",
    ),
}


def style_like(ws, src_row, dst_row, ncols):
    for c in range(1, ncols + 1):
        s = ws.cell(row=src_row, column=c)
        d = ws.cell(row=dst_row, column=c)
        if s.has_style:
            d.font = copy.copy(s.font)
            d.border = copy.copy(s.border)
            d.fill = copy.copy(s.fill)
            d.alignment = copy.copy(s.alignment)
            d.number_format = s.number_format
            d.protection = copy.copy(s.protection)


def add_link(cell, sheet, row):
    cell.hyperlink = Hyperlink(ref=cell.coordinate, location="'%s'!B%d" % (sheet, row))


def diff_excerpt(old, new, ctx=14):
    """把「旧 → 新」压成一段带上下文的短摘要，便于在「数据说明」里逐行核对。"""
    old = old or ""
    new = new or ""
    if old == new:
        return ""
    if old.rstrip("\n") == new.rstrip("\n"):
        return "仅结尾换行差异（旧多一个换行）"
    n = min(len(old), len(new))
    i = 0
    while i < n and old[i] == new[i]:
        i += 1
    head = old[max(0, i - ctx):i]
    return "…%s「%s」→「%s」…" % (head.replace("\n", "⏎"),
                                 old[i:i + ctx * 2].replace("\n", "⏎"),
                                 new[i:i + ctx * 2].replace("\n", "⏎"))


def num_or_none(v):
    if v in (None, "", '""'):
        return None
    try:
        f = float(v)
    except (TypeError, ValueError):
        return v
    return int(f) if f == int(f) else round(f, 2)


def main():
    dry = "--dry" in sys.argv
    print("=== 0.32 白卡标签表更新 ===")
    wb = openpyxl.load_workbook(XLSX_ROOT)
    tr = F.load_translate()
    idx = F.packet_index()
    texts = Z.load()
    white = F.white_list()
    print("解包 White 白卡：%d 株；图鉴条目：%d 条" % (len(white), len(texts)))

    ws = wb["白卡植物标签"]
    wf = wb["标签筛选"]
    wt = wb["图鉴说明"]
    wd0 = wb["标签字典"]

    rows = [r for r in ws.iter_rows(min_row=2, values_only=True) if r and r[2]]
    have = [str(r[2]).strip() for r in rows]
    todo = [k for k in white if k not in have]
    print("表内现有 %d 株，待补 %d 株：%s" % (len(rows), len(todo), todo))
    lack = [k for k in todo if k not in NEW_ROWS]
    if lack:
        print("!! 这些新卡没有核定标签：", lack)
        return 1

    facts = {k: F.facts_of(k, idx, tr, "") for k in (set(todo) | set(NEW_ROWS))}

    # ── 1) 白卡植物标签 ──
    seq = max(int(r[0]) for r in rows)
    r = ws.max_row
    for k in todo:
        f = facts[k]
        m = NEW_ROWS[k]
        seq += 1
        r += 1
        vals = [seq, m["cn"], k, num_or_none(f.get("cost")),
                num_or_none(f.get("cooldown")),
                num_or_none(f.get("hitpoints")) or 300,
                m["attack"], "、".join(m["tags"]), m["evidence"]]
        for c, v in enumerate(vals, start=1):
            ws.cell(row=r, column=c, value=v)
        style_like(ws, r - 1, r, 9)
        print("  + [%d] %s %s 阳光=%s 冷却=%s 韧性=%s 攻击=%s 标签=%s"
              % (seq, m["cn"], k, vals[3], vals[4], vals[5], m["attack"], vals[7]))

    # ── 1b) 用户指定追加标签（USER_PATCHES）：改「标签」列 + 依据列 + 标签筛选 + 标签字典示例/说明 ──
    # 标签插入顺序沿用表内习惯：攻击 → 职能 → 生产 → 系列 → 属性（组序取自「标签字典」B 列）
    GROUP_RANK = {"攻击": 0, "职能": 1, "生产": 2, "系列": 3, "属性": 4}
    tag_group = {}
    for rr0 in wd0.iter_rows(min_row=2, max_row=wd0.max_row, values_only=True):
        if rr0 and rr0[0]:
            tag_group[str(rr0[0]).strip()] = GROUP_RANK.get(str(rr0[1] or "").strip(), 9)

    patched = []
    for pkey, pp in USER_PATCHES.items():
        prow = next((rr for rr in ws.iter_rows(min_row=2, max_row=ws.max_row)
                     if str(rr[2].value or "").strip() == pkey), None)
        if prow is None:
            print("!! 补标对象不在表里：", pkey)
            return 1
        cur_tags = [t.strip() for t in str(prow[7].value or "").replace("，", "、").split("、") if t.strip()]
        new_tags = list(cur_tags)
        for tg in pp["add_tags"]:
            if tg in new_tags:
                continue
            if tg not in tag_group:
                print("!! 标签不在「标签字典」里：%s" % tg)
                return 1
            rank = tag_group[tg]
            pos = len(new_tags)
            for i, t0 in enumerate(new_tags):
                if tag_group.get(t0, 9) > rank:
                    pos = i
                    break
            new_tags.insert(pos, tg)
        if new_tags != cur_tags:
            prow[7].value = "、".join(new_tags)
            patched.append((pkey, str(prow[1].value), cur_tags, new_tags, str(prow[0].value)))
            print("  ~ 标签补标 %s：%s → %s" % (pkey, "、".join(cur_tags), "、".join(new_tags)))
        # 依据列：按 USER_PATCHES.replace 改写说法（幂等：已改过就不再匹配）
        ev = str(prow[8].value or "")
        for old_s, new_s in pp.get("replace", []):
            if old_s in ev:
                prow[8].value = ev.replace(old_s, new_s)
                print("    依据列已更新（%s → %s…）" % (old_s[:12], new_s[:18]))
                ev = str(prow[8].value)
        # 「标签筛选」：给该株补一行（内鬼盒子是最后一株 ⇒ 其块就在表尾，直接追加）
        pseq = prow[0].value
        block = [rr for rr in wf.iter_rows(min_row=2, max_row=wf.max_row) if rr[0].value == pseq]
        want = sorted(new_tags)
        have = [str(rr[2].value).strip() for rr in block]
        for tg in new_tags:
            if tg in have:
                continue
            if not block or block[-1][0].row != wf.max_row:
                print("!! 「标签筛选」里 %s 的块不在表尾，追加会错位，已跳过（请手工处理）" % pkey)
                continue
            frow2 = wf.max_row + 1
            vals = [pseq, prow[1].value, tg, prow[3].value, prow[4].value, prow[5].value, prow[6].value]
            for c, v in enumerate(vals, start=1):
                wf.cell(row=frow2, column=c, value=v)
            style_like(wf, frow2 - 1, frow2, 7)
            add_link(wf.cell(row=frow2, column=2), "图鉴说明",
                     next(rr[0].row for rr in wt.iter_rows(min_row=2, max_row=wt.max_row)
                          if str(rr[2].value or "").strip() == pkey))
            print("    「标签筛选」+1 行 [%s] %s %s" % (pseq, prow[1].value, tg))

    # ── 2) 图鉴说明（追加新卡 + 修 0.31 的 14 行故事错位）──
    trow = wt.max_row
    seq_of = {}
    for rr in ws.iter_rows(min_row=2, values_only=True):
        if rr and rr[2]:
            seq_of[str(rr[2]).strip()] = rr[0]
    for k in todo:
        m = NEW_ROWS[k]
        t = texts.get(m["cn"], {})
        trow += 1
        vals = [seq_of[k], m["cn"], k, t.get("intro", ""), t.get("detail", ""), t.get("story", "")]
        for c, v in enumerate(vals, start=1):
            wt.cell(row=trow, column=c, value=v)
        style_like(wt, trow - 1, trow, 6)
        print("  + 图鉴说明[%d] %s 简介=%d字 属性=%d字 故事=%d字"
              % (trow, m["cn"], len(vals[3] or ""), len(vals[4] or ""), len(vals[5] or "")))

    # 0.31 那轮由 update_table_031.py 补的 14 株（当时 zh_texts 取 fields[i-1]，故事整体错位一格）
    import importlib
    _m031 = importlib.import_module("update_table_031")
    buggy_keys = set(_m031.NEW_ROWS)
    fixed_story = []
    for rr in wt.iter_rows(min_row=2, max_row=wt.max_row):
        key = str(rr[2].value or "").strip()
        cn = str(rr[1].value or "").strip()
        if key not in buggy_keys or not cn:
            continue
        t = texts.get(cn)
        if not t or not t.get("story"):
            continue
        cur = str(rr[5].value or "")
        if cur != t["story"]:
            fixed_story.append((rr[0].row, key, cn, cur[:24], t["story"][:24]))
            rr[5].value = t["story"]
    print("修正图鉴故事行数：%d" % len(fixed_story))
    for x in fixed_story:
        print("   行%-4d %-22s %-8s 旧=%-26s 新=%s" % (x[0], x[1], x[2], x[3], x[4]))

    # ── 2c) 回填 0.32 对既有白卡改写的图鉴文案（简介 / 属性 / 故事）──
    # 只在显式给 --backfill-text 时执行：这属于「把 0.31 的旧文案换成 0.32 的」，
    # 不属于补新卡，单独开关避免日常复跑时静默改写既有行。
    backfilled = []
    if "--backfill-text" in sys.argv:
        for rr in wt.iter_rows(min_row=2, max_row=wt.max_row):
            key = str(rr[2].value or "").strip()
            cn = str(rr[1].value or "").strip()
            t = texts.get(cn)
            if not key or not t:
                continue
            for col, kind, field in ((3, "简介", "intro"), (4, "属性", "detail"), (5, "故事", "story")):
                cur = str(rr[col].value or "")
                new = t.get(field) or ""
                if not new or cur == new:
                    continue
                rr[col].value = new
                backfilled.append((rr[0].row, rr[0].value, kind, key, cn, cur, new))
        print("0.32 文案回填行数：%d" % len(backfilled))
        for (ri, seq, kind, key, cn, cur, new) in backfilled:
            print("   行%-4d [%s] 序号%s %-8s %s" % (ri, kind, seq, cn, diff_excerpt(cur, new)))

    # ── 2b) 补三表互跳超链接（B 列）──
    row_of = {}          # 植物键 → 白卡植物标签 行号
    for rr in ws.iter_rows(min_row=2, max_row=ws.max_row):
        if rr[2].value:
            row_of[str(rr[2].value).strip()] = rr[0].row
    trow_of = {}
    for rr in wt.iter_rows(min_row=2, max_row=wt.max_row):
        if rr[2].value:
            trow_of[str(rr[2].value).strip()] = rr[0].row
    n_link = 0
    for k, rw in row_of.items():
        c = ws.cell(row=rw, column=2)
        if c.hyperlink is None:
            add_link(c, "图鉴说明", trow_of[k]); n_link += 1
    for k, rw in trow_of.items():
        c = wt.cell(row=rw, column=2)
        if c.hyperlink is None:
            add_link(c, "白卡植物标签", row_of[k]); n_link += 1
    seq_key = {}
    for k, rw in row_of.items():
        seq_key[ws.cell(row=rw, column=1).value] = k
    for rr in wf.iter_rows(min_row=2, max_row=wf.max_row):
        k = seq_key.get(rr[0].value)
        if k is None:
            continue
        if rr[1].hyperlink is None:
            add_link(rr[1], "图鉴说明", trow_of[k]); n_link += 1
    print("补齐超链接单元格数：%d" % n_link)

    # ── 3) 标签筛选（追加新卡的逐标签行）──
    frow = wf.max_row
    for k in todo:
        m = NEW_ROWS[k]
        f = facts[k]
        for tag in sorted(m["tags"]):
            frow += 1
            vals = [seq_of[k], m["cn"], tag, num_or_none(f.get("cost")),
                    num_or_none(f.get("cooldown")),
                    num_or_none(f.get("hitpoints")) or 300, m["attack"]]
            for c, v in enumerate(vals, start=1):
                wf.cell(row=frow, column=c, value=v)
            style_like(wf, frow - 1, frow, 7)
            add_link(wf.cell(row=frow, column=2), "图鉴说明", trow_of[k])
    print("标签筛选新增行：%d（表尾行号 %d）" % (sum(len(NEW_ROWS[k]["tags"]) for k in todo), frow))

    # ── 4) 按章节分类（整表重建）──
    name = "按章节分类"
    if name in wb.sheetnames:
        del wb[name]
    wc = wb.create_sheet(name, wb.sheetnames.index("图鉴说明") + 1)
    head = ["章节分组", "排序权重", "序号", "植物名称", "植物键", "阳光", "冷却(秒)", "韧性",
            "攻击/弹道", "标签", "获取方式（图鉴原文）"]
    wc.append(head)
    for c in range(1, len(head) + 1):
        cell = wc.cell(row=1, column=c)
        cell.font = Font(bold=True)
        cell.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        cell.fill = PatternFill("solid", fgColor="DDEBF7")

    def acq_of(detail):
        if not detail:
            return ""
        mm = re.search(r"获取方式[：:]\s*([^\n\r]*)", detail)
        return mm.group(1).strip() if mm else ""

    detail_of = {}
    for rr in wt.iter_rows(min_row=2, values_only=True):
        if rr and rr[2]:
            detail_of[str(rr[2]).strip()] = rr[4]
    out_rows = []
    for rr in ws.iter_rows(min_row=2, values_only=True):
        if not rr or not rr[2]:
            continue
        key = str(rr[2]).strip()
        acq = acq_of(detail_of.get(key))
        g = classify(acq)
        out_rows.append((GROUP_LABEL[g], GROUP_WEIGHT[g], rr[0], rr[1], key, rr[3], rr[4], rr[5],
                         rr[6], rr[7], acq))
    out_rows.sort(key=lambda x: (x[1], x[2] if isinstance(x[2], int) else 9999))
    for v in out_rows:
        wc.append(list(v))
    wc.freeze_panes = "A2"
    for col, w in zip("ABCDEFGHIJK", [12, 10, 8, 16, 26, 8, 10, 8, 12, 40, 40]):
        wc.column_dimensions[col].width = w
    from collections import Counter
    cnt = Counter(v[0] for v in out_rows)
    print("按章节分类重建：%d 行" % len(out_rows))
    for g, label, _ in GROUPS:
        if cnt.get(label):
            print("   %-10s %3d 株" % (label, cnt[label]))

    # ── 5) 标签字典：重算命中植物数 + 补 0.32 判定说明 ──
    tag_cnt = {}
    for rr in ws.iter_rows(min_row=2, max_row=ws.max_row, values_only=True):
        if not rr or not rr[7]:
            continue
        for t in str(rr[7]).replace("，", "、").split("、"):
            t = t.strip()
            if t:
                tag_cnt[t] = tag_cnt.get(t, 0) + 1
    wd0 = wb["标签字典"]
    changed = []
    for rr in wd0.iter_rows(min_row=2, max_row=wd0.max_row):
        tag = str(rr[0].value or "").strip()
        if not tag:
            continue
        new = tag_cnt.get(tag, 0)
        old = rr[6].value
        if str(old).strip() != str(new):
            rr[6].value = str(new) if new else ""
            changed.append((tag, old, new))
    print("标签字典命中数变动：%d 项 %s" % (len(changed), changed))

    NOTES = {
        "击退": "0.32 起有源码依据：弹道资源 hitTargetEventList 含 TowerDefenseCharacterEventHitBack"
               "（叶子卷心菜投手 length=0.1）；前两株仍属人工指定",
        "缠绕": "0.32 海草咖啡豆无 TanglekelpComponent，但脚本用 HitBoxSuppressionReason.Tanglekelp 把僵尸拖入水，"
               "按行为并入本标签",
        "窝瓜": "0.32 窝瓜炸弹本体无 SquashComponent，脚本在 3×3 每格种出 PlantSquash，按行为并入本标签",
        "南瓜壳": "0.32 西瓜棚：图鉴「特性：南瓜壳」+ plantGridType=[7]（与全部南瓜壳系一致）",
        "产阳光": "0.32 新增 produceType=IceSun / DoomSun（均在 ProduceComponent.cs 走 TowerDefenseSunBase，"
                 "SunDropKind.IceSun/DoomSun）⇒ 仍计产阳光",
        "灯具": "0.32 路灯毁灭菇：脚本调用 AddDoomPlanternLight()（路灯花照明）+ 图鉴「特性：路灯」",
        "一次性": "0.32 毁灭向日葵/内鬼盒子/路灯毁灭菇/窝瓜炸弹均含 ExplodeComponent（按组件判定）",
    }
    # 用户指定补标的标签：把说明与示例植物也同步进「标签字典」
    for pp in USER_PATCHES.values():
        for tg in pp["add_tags"]:
            if pp.get("dict_note"):
                NOTES[tg] = pp["dict_note"]
    for rr in wd0.iter_rows(min_row=2, max_row=wd0.max_row):
        tag = str(rr[0].value or "").strip()
        if tag in NOTES:
            cur = str(rr[5].value or "").strip()
            add = "【0.32】" + NOTES[tag]
            if add not in cur:
                rr[5].value = (cur + "；" + add) if cur else add
                print("  标签字典补充说明：%s" % tag)
    for (pkey, pcn, _cur, _new, _seq) in patched:
        for tg in USER_PATCHES[pkey]["add_tags"]:
            for rr in wd0.iter_rows(min_row=2, max_row=wd0.max_row):
                if str(rr[0].value or "").strip() != tg:
                    continue
                ex = str(rr[7].value or "").strip()
                if pcn and pcn not in ex:
                    rr[7].value = (ex.rstrip("、") + "、" + pcn).strip("、")
                    print("  标签字典示例植物：%s += %s" % (tg, pcn))

    # ── 6) 数据说明 ──
    wd = wb["数据说明"]
    def upsert(k, v):
        for rr in wd.iter_rows(min_row=2, max_row=wd.max_row):
            if str(rr[0].value or "").strip() == k:
                rr[1].value = v
                return
        wd.append([k, v])

    upsert("数据来源", "《植物大战僵尸杂交重制版》(0.32.0) 解包工程 unpack\\"
                       "（工作区根目录；0.31 旧解包留档于 _ref_031_unpack\\，仅作版本对比）")
    upsert("白卡口径", "卡池 Asset/Config/PacketBank/PacketBankResource.json → GeneralPlant.Category.White，"
                       "共 %d 株（0.31 为 202 株）" % len(white))
    upsert("白卡总数", "%d 株（White 卡池数组顺序；含 0.32 新增 9 株）" % len(white))
    upsert("图鉴顺序", "202 株基线按 GeneralPlant.White 顺序排列；0.31/0.32 新增白卡一律追加在表尾，"
                       "故表内行序与卡池数组位置不再严格一一对应（对照见「0.32 行序说明」）")
    # ⑥ 的数目：本次真回填了几行，或表里已有几条回填记录（幂等复跑时别把它从说明里抹掉）
    n_back = len(backfilled) or sum(1 for r_ in wd.iter_rows(min_row=2, max_row=wd.max_row)
                                    if str(r_[0].value or "").startswith("0.32 回填-"))
    # 补标是否已在表里（幂等复跑时 patched 为空，但说明里仍要保留 ⑦）
    patch_rows = []
    for pkey, pp in USER_PATCHES.items():
        prow = next((rr for rr in ws.iter_rows(min_row=2, max_row=ws.max_row)
                     if str(rr[2].value or "").strip() == pkey), None)
        if prow is None:
            continue
        have_tags = [t.strip() for t in str(prow[7].value or "").replace("，", "、").split("、") if t.strip()]
        if all(tg in have_tags for tg in pp["add_tags"]):
            patch_rows.append((pkey, str(prow[1].value), prow[0].value))
    upsert("本次更新", "0.32.0：①补齐 9 株新白卡（表尾序号 203~211）并逐株打标签；"
                       "②修正 0.31 那轮 14 行「图鉴故事」错位（旧工具取了上一株的故事）；"
                       "③「标签字典」重算命中植物数并补 7 条 0.32 判定说明；"
                       "④「按章节分类」新增分组「内鬼盒子无尽版」；⑤补齐 23 株植物在三张表里的互跳超链接"
                       + ("；⑥回填 0.32 改写的既有图鉴文案 %d 行" % n_back if n_back else "")
                       + ("；⑦按用户要求给%s追加「%s」（人工指定）" %
                          ("、".join(p[1] for p in patch_rows),
                           "、".join(tg for p in patch_rows for tg in USER_PATCHES[p[0]]["add_tags"]))
                          if patch_rows else ""))
    if patch_rows:
        # 「人工指定」行追加说明（幂等：已含则不重复加）
        for rr in wd.iter_rows(min_row=2, max_row=wd.max_row):
            if str(rr[0].value or "").strip() == "人工指定":
                txt = str(rr[1].value or "")
                for (pkey, _pcn, _seqv) in patch_rows:
                    piece = USER_PATCHES[pkey]["desc"]
                    if piece not in txt:
                        rr[1].value = txt + "；" + piece
                        print("  数据说明「人工指定」行 += %s" % piece)
                break
        upsert("0.32 人工指定",
               "；".join("用户指定：%s（%s，序号 %s）追加「%s」" % (pcn, pkey, seqv, "、".join(USER_PATCHES[pkey]["add_tags"]))
                        for (pkey, pcn, seqv) in patch_rows)
               + "。事实核对：内鬼盒子的图鉴（简介「有着两副面孔、四种结果的神秘盒子」/特性「随机礼盒」）"
                 "与脚本 TowerDefensePlantPresentNG.cs 里都没有「模仿」字样，行为是开盒随机抽 4 种结果"
                 "（随机植物／被魅惑随机植物／随机非BOSS僵尸／被魅惑随机非BOSS僵尸）；"
                 "但它复用了抽奖盒子 TowerDefensePlantPresentBox.GetReplacementPlantBank() / "
                 "CreateReplacementPacket() / SpawnReplacement() 的替换管线，而抽奖盒子的「模仿者」"
                 "本来就是人工指定 ⇒ 与本表既有口径一致，最终仍按用户要求打标。")
    upsert("0.32 新增白卡",
           "共 %d 株（按标签字典口径逐株打标签，每条都写了源码/图鉴依据）：%s"
           % (len(NEW_ROWS), "、".join(NEW_ROWS[k]["cn"] for k in NEW_ROWS)))
    upsert("按章节分类", "按「图鉴说明」的「获取方式」分组：冒险第1~10章 / 杂交乐园 / 在线关卡 / 商店购买 / "
                         "抽奖盒子 / 迷你游戏 / 挑战模式 / 我是僵尸 / 对战模式 / 内鬼盒子无尽版 / 限时活动 / 初始可用"
                         "（「内鬼盒子无尽版」为 0.32 新增分组：内鬼盒子的获取方式＝通关内鬼盒子无尽版 10 回合）")
    upsert("文案抽取", "Translate.zh.translation（Godot OptimizedTranslation）：0.32 起 messages 段改为明文存放"
                       "（文件首魔数 RSRC、整文件查不到 zstd 帧），按 \\x00 字段边界切块，字段序为 "
                       "[名字][简介][属性块][故事]；0.31 为 RSCC + 逐帧 zstd 压缩，需先解压再切块")
    upsert("源码根目录 U", "U = 本工作区 unpack\\（0.32.0）；「标签字典」表里「源码查找路径」列的 U 即此根目录"
                           "（0.31 期初为 C:\\Users\\txgcs\\Downloads\\植物大战僵尸杂交重制版\\解包）")
    for i, k in enumerate(list(NEW_ROWS), start=1):
        m = NEW_ROWS[k]
        f = facts[k]
        # 以表内实际值为准（USER_PATCHES 追加的标签 / 改写过的依据都要反映进来）
        cur_row = next((rr for rr in ws.iter_rows(min_row=2, max_row=ws.max_row)
                        if str(rr[2].value or "").strip() == k), None)
        tags_now = str(cur_row[7].value) if cur_row is not None else "、".join(m["tags"])
        ev_now = str(cur_row[8].value) if cur_row is not None else m["evidence"]
        upsert("0.32-%d %s（%s）" % (i, m["cn"], k),
               "标签：%s ｜ 攻击/弹道：%s ｜ 阳光=%s 冷却=%s 韧性=%s ｜ 依据：%s"
               % (tags_now, m["attack"],
                  num_or_none(f.get("cost")) if f.get("cost") else "—",
                  num_or_none(f.get("cooldown")),
                  num_or_none(f.get("hitpoints")) or 300,
                  ev_now))
    upsert("0.32 行序说明",
           "0.32 的 9 株在卡池 GeneralPlant.White 数组里是插在中间（第 195~198、206~211 位），"
           "本表沿用 0.31 起的做法追加在表尾（序号 203~211），不重排既有 202 行以免序号漂移；"
           "查表时请用「植物名称/植物键」定位，不要用行号对游戏图鉴。"
           "注：0.31 那轮同样把新增 14 株追加在表尾，因此表内第 142~202 行与数组位置的偏移在 0.31 就已存在")
    upsert("0.32 判定补充",
           "击退：改为可源码判定（弹道 hitTargetEventList 含 TowerDefenseCharacterEventHitBack）；"
           "缠绕：海草咖啡豆按 HitBoxSuppressionReason.Tanglekelp 行为并入；"
           "窝瓜：窝瓜炸弹按脚本种出 PlantSquash 并入；"
           "南瓜壳：西瓜棚按图鉴「特性：南瓜壳」+ plantGridType=[7] 并入；"
           "产阳光：IceSun/DoomSun 均属 SunDropKind 阳光类，照旧计产阳光")
    upsert("0.32 文案修正",
           "0.31 那轮补的 14 行（序号 189~202）「图鉴故事」错位一格（当时取了上一株的故事），本轮已按 "
           "Translate 字段 [名字][简介][属性][故事] 重新取 i+3 修正；简介/属性未动")
    # 「0.32 未回填」→ 回填后改名 / 移除
    def drop_row(k):
        for rr in list(wd.iter_rows(min_row=2, max_row=wd.max_row)):
            if str(rr[0].value or "").strip() == k:
                wd.delete_rows(rr[0].row, 1)
                return True
        return False

    if backfilled:
        drop_row("0.32 未回填")
        kinds = {}
        for (_, _, kind, _, _, _, _) in backfilled:
            kinds[kind] = kinds.get(kind, 0) + 1
        upsert("0.32 文案回填",
               "已回填 0.32 改写的图鉴文案 %d 行（属性 %d／故事 %d／简介 %d），数据取自 0.32 的 "
               "Translate.zh.translation 同一条目（已去 BBCode）；逐行改动见下 %d 条，"
               "格式为「…上下文「旧」→「新」…」（⏎ 表示换行）"
               % (len(backfilled), kinds.get("属性", 0), kinds.get("故事", 0), kinds.get("简介", 0),
                  len(backfilled)))
        for i, (ri, seq, kind, key, cn, cur, new) in enumerate(backfilled, start=1):
            upsert("0.32 回填-%02d %s（序号%s·%s）" % (i, cn, seq, kind),
                   "%s" % diff_excerpt(cur, new))
    else:
        already = any(str(r[0].value or "").startswith("0.32 回填-")
                      for r in wd.iter_rows(min_row=2, max_row=wd.max_row))
        if not already:
            upsert("0.32 回填提示",
                   "未执行文案回填（加 --backfill-text 运行本脚本即可回填 0.32 对既有白卡改写的简介/属性/故事）")
    upsert("标签数", "使用中的标签 %d 个（「标签字典」共 %d 条定义，其中 %d 条白卡暂无命中）；"
                     "本轮未新增标签，只给 7 个已有标签补了 0.32 判定说明"
           % (len(tag_cnt), wd0.max_row - 1, (wd0.max_row - 1) - len(tag_cnt)))

    print("\n表格统计：白卡 %d 株；标签筛选 %d 行；图鉴说明 %d 行；按章节分类 %d 行；标签 %d 个"
          % (ws.max_row - 1, wf.max_row - 1, wt.max_row - 1, wc.max_row - 1, len(tag_cnt)))

    if dry:
        print("\n--dry：不写盘")
        return 0

    # 备份（只在备份不存在时建，避免覆盖第一次的原始版本）
    for src, bak in ((XLSX_ROOT, BAK_ROOT), (XLSX_DATA, BAK_DATA)):
        if os.path.isfile(src) and not os.path.isfile(bak):
            shutil.copyfile(src, bak)
            print("已备份:", bak)
    wb.save(XLSX_ROOT)
    shutil.copyfile(XLSX_ROOT, XLSX_DATA)
    print("\n已写入:", XLSX_ROOT)
    print("已同步:", XLSX_DATA)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
