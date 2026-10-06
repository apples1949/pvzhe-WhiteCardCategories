using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// 白卡标签的**运行时自动识别**（依据《白卡植物标签表.xlsx》「标签字典」的判定依据）。
///
/// 用途：烘焙表（<see cref="WhiteCardCategoriesTable.PlantTagLines"/>）只覆盖制表时的
/// 188 株白卡；**游戏更新或其它 Mod 新增的白卡不在表里** ⇒ 由本类按同样的规则兜底，
/// 仍判不出的由调用方丢进「其他」，**不会漏卡**。
///
/// ── 能自动识别的判据（按表格「标签字典」）──────────────────────────
/// ① **配置字段**（`TowerDefensePlantConfig`，卡池里就能读到，最稳）：
///      physiqueTypeFlags → 坚果/花盆/地刺/猫尾/灯具/魔法/咖啡豆/辣椒/莲叶/机器/花瓶/不可穿透
///      elementFlags      → 冰/火
///      plantGridType     → 水生（仅 WATER）/ 水陆（含 WATER）
///      sleepTime=="Day"  → 夜行
///      plantCover 非空   → 仅底座
///      hitpoints>=3000   → 坚果（表格对「高韧性」的补充口径）
/// ② **组件集**（`CharacterComponentSet`，按角色场景路径推导后 `Load`）：
///      ExplodeComponent→一次性 · AttackComponent→近战 · MousePress/Cannon→发射
///      PeriodicAreaEvent→反击 · ChangeProjectile→火炬 · Potato→地雷 · Magnet→磁力
///      Block→保护伞 · Blover→吹飞 · Squash→窝瓜 · GrowUp→成长 · Tanglekelp→缠绕
///      Chomper→大嘴花 · Gravebuster→墓吞 · Scared→胆小 · Slot→花盆
///      Bowling→滚动攻击 · MagnetCoin→吸金/产资源
///      ProduceComponent 的 produceType → 产阳光（Sun/BrainSun/JalaSun/QXSun/MagicSun）/ 产资源（其余）
/// ③ **名称关键词**（植物键小写）→ 系列标签（豌豆/向日葵/寒冰/大蒜/樱桃/玉米/…）
///
/// ── 为什么不读弹道字段（射击/投抛/穿透/猫尾/后向/滚动弹道）──────────
///   那几项要钻进 `FireComponentDefinition` 里内嵌的 `TowerDefenseProjectileCreateData`
///   （子资源嵌套两层），而且表格里这 6 个标签在 188 株白卡上**已全部烘焙**。
///   为避免运行时深挖嵌套资源带来的开销与版本脆弱性，这里只用名称关键词近似
///   （pult/cob/cannon→投抛，cat/tail→猫尾，spike→穿透 等），并保留烘焙表为准。
/// </summary>
internal static class WhiteCardAutoTagger
{
	// ── physiqueTypeFlags 位值（源码 TowerDefenseEnum.CHARACTER_PHYSIQUE_TYPE）──
	private const int PhyNut = 1;
	private const int PhyPot = 2;
	private const int PhyLilypad = 4;
	private const int PhyCoffee = 8;
	private const int PhySpike = 0x10;
	private const int PhyVase = 0x20;
	private const int PhyLight = 0x40;
	private const int PhyJalapeno = 0x80;
	private const int PhyCat = 0x100;
	private const int PhyCantPenetrate = 0x400;
	private const int PhyMachine = 0x800;
	private const int PhyMagic = 0x1000;

	// ── elementFlags（ELEMENT_SYSTEM）──
	private const int ElIce = 1;
	private const int ElFire = 2;

	// ── plantGridType（PLANTGRIDTYPE）──
	private const int GtWater = 3;

	/// <summary>高韧性补充判定（与表格「坚果」的补充口径一致）。</summary>
	private const double NutHitpoints = 3000.0;

	/// <summary>组件集缓存：角色名 → 组件类型 Id 集合（null = 取不到）。</summary>
	private static readonly Dictionary<string, HashSet<string>> _compCache
		= new Dictionary<string, HashSet<string>>();

	/// <summary>Produce 组件的产出类型缓存：角色名 → produceType（小写）。</summary>
	private static readonly Dictionary<string, string> _produceCache
		= new Dictionary<string, string>();

	/// <summary>
	/// 名称关键词 → 系列标签（依据表格「标签字典」的「名称类判定」）。
	/// ⚠️ 全部小写比较（本项目踩过 "Pult" 漏掉 "pult" 的大小写坑）。
	/// 顺序：先匹配到的系列标签都会加上（一株可属多个系列）。
	/// </summary>
	private static readonly (string Tag, string[] Kw)[] SeriesRules = new (string, string[])[]
	{
		("豌豆",     new[] { "pea", "peater", "gatling", "kabbage" }),
		("向日葵",   new[] { "sunflower", "sunfl" }),
		("寒冰",     new[] { "snow" }),
		("大蒜",     new[] { "garlic" }),
		("樱桃",     new[] { "cherry" }),
		("玉米",     new[] { "corn" }),
		("南瓜壳",   new[] { "pumpkin" }),
		("杨桃",     new[] { "star" }),
		("僵尸",     new[] { "zombie" }),
		("灯具",     new[] { "plantern", "lantern", "lamp", "light" }),
		("土豆雷",   new[] { "potatomine", "wildmine", "magnetmine", "cherrymine" }),
		("三叶草",   new[] { "blover" }),
		("喷菇",     new[] { "fume", "shroom" }),
		("树桩",     new[] { "wood" }),
		("毁灭",     new[] { "doom", "taboo" }),
		("全息",     new[] { "hologram", "holograph" }),
		("仙人掌",   new[] { "cactus" }),
		("忧郁",     new[] { "gloom" }),
		("促销",     new[] { "sale" }),
		("高坚果",   new[] { "tallnut" }),
		("小喷菇",   new[] { "puff" }),
		("裂荚",     new[] { "split" }),
		("西瓜",     new[] { "melon" }),
		("模仿者",   new[] { "imitater" }),
		("咖啡豆",   new[] { "coffee" }),
		("辣椒",     new[] { "jala" }),
		("魔法",     new[] { "magic" }),
		("海兵菇",   new[] { "sealeaf", "iceseashroom" }),
	};

	/// <summary>弹道/攻击方式的**近似**关键词（精确判据是弹道内嵌字段，见类注释）。</summary>
	private static readonly (string Tag, string[] Kw)[] AttackKwRules = new (string, string[])[]
	{
		("投抛", new[] { "pult", "cob", "cannon", "mortar" }),
		("猫尾", new[] { "cattail", "catpot", "catflower", "catpumpkin", "cat" }),
		("射击", new[] { "shooter", "pea", "gatling", "repeater", "pult" }),
		("穿透", new[] { "spike", "cactus", "penetrate" }),
	};

	/// <summary>
	/// 给一张卡打自动标签（结果并入 <paramref name="outTags"/>；调用方自行与烘焙表合并）。
	/// 全程不抛异常。
	/// </summary>
	internal static void Tag(string plantKey, TowerDefensePlantConfig pc, HashSet<string> outTags)
	{
		try
		{
			if (outTags == null || string.IsNullOrEmpty(plantKey))
			{
				return;
			}
			string lk = plantKey.ToLowerInvariant();

			// ② 名称 → 系列
			foreach ((string tag, string[] kws) in SeriesRules)
			{
				foreach (string w in kws)
				{
					if (lk.Contains(w))
					{
						outTags.Add(tag);
						break;
					}
				}
			}
			// 僵尸化的 key 常以 Z 结尾（PlantPeashooterZ / PlantCatTailZ …）
			if (lk.EndsWith("z"))
			{
				outTags.Add("僵尸");
			}

			// ③ 攻击方式近似
			foreach ((string tag, string[] kws) in AttackKwRules)
			{
				foreach (string w in kws)
				{
					if (lk.Contains(w))
					{
						outTags.Add(tag);
						break;
					}
				}
			}

			// ① 配置字段
			if (pc != null && GodotObject.IsInstanceValid(pc))
			{
				if (pc.hitpoints >= NutHitpoints)
				{
					outTags.Add("坚果");
				}
				int phy = pc.physiqueTypeFlags;
				if ((phy & PhyNut) != 0) { outTags.Add("坚果"); }
				if ((phy & PhyPot) != 0) { outTags.Add("花盆"); }
				if ((phy & PhySpike) != 0) { outTags.Add("地刺"); }
				if ((phy & PhyCat) != 0) { outTags.Add("猫尾"); }
				if ((phy & PhyLight) != 0) { outTags.Add("灯具"); }
				if ((phy & PhyMagic) != 0) { outTags.Add("魔法"); }
				if ((phy & PhyCoffee) != 0) { outTags.Add("咖啡豆"); }
				if ((phy & PhyJalapeno) != 0) { outTags.Add("辣椒"); }
				if ((phy & PhyLilypad) != 0) { outTags.Add("莲叶"); }
				if ((phy & PhyMachine) != 0) { outTags.Add("机器"); }
				if ((phy & PhyVase) != 0) { outTags.Add("花瓶"); }
				if ((phy & PhyCantPenetrate) != 0) { outTags.Add("不可穿透"); }

				int el = pc.elementFlags;
				if ((el & ElIce) != 0) { outTags.Add("冰"); }
				if ((el & ElFire) != 0) { outTags.Add("火"); }

				try
				{
					if (!string.IsNullOrEmpty(pc.sleepTime)
						&& pc.sleepTime.Equals("Day", StringComparison.OrdinalIgnoreCase))
					{
						outTags.Add("夜行");
					}
				}
				catch { }

				try
				{
					int waterCnt = 0, gridCnt = 0;
					foreach (TowerDefenseEnum.PLANTGRIDTYPE g in pc.plantGridType)
					{
						gridCnt++;
						if ((int)g == GtWater)
						{
							waterCnt++;
						}
					}
					if (waterCnt > 0)
					{
						// 只有 WATER ⇒ 水生；含 WATER 但还有别的 ⇒ 水陆（与表格口径一致）
						outTags.Add((waterCnt == gridCnt) ? "水生" : "水陆");
					}
				}
				catch { }

				try
				{
					if (pc.plantCover != null && pc.plantCover.Count > 0)
					{
						outTags.Add("仅底座");
					}
				}
				catch { }

				// ② 组件集
				TagByComponents(pc.name, outTags);
			}
		}
		catch { }
	}

	/// <summary>
	/// 按**角色场景路径**推导并加载 `CharacterComponentSet`，读组件类型 Id。
	///
	/// 路径推导依据（实测的导出结构）：
	///   角色场景 `.../Scene/TowerDefensePlant&lt;X&gt;.tscn` 的 ext_resource **直接引用**
	///   `.../Scene/TowerDefensePlant&lt;X&gt;ComponentSet.tres`（同目录同名）
	///   ⇒ 把 `.tscn` 换成 `ComponentSet.tres` 即可。
	/// 角色名 = `TowerDefensePlantConfig.name`（实测 = 植物键），
	/// 经 `ResourceManager.GetCharacterScene(name)` 拿到 PackedScene 及其 `ResourcePath`。
	///
	/// ⚠️ 每株只解析一次并缓存；失败也缓存（避免新卡每帧重试）。
	/// </summary>
	private static void TagByComponents(string charName, HashSet<string> outTags)
	{
		try
		{
			if (string.IsNullOrEmpty(charName))
			{
				return;
			}
			HashSet<string> comps;
			string produceType;
			if (!_compCache.TryGetValue(charName, out comps))
			{
				comps = LoadComponentIds(charName, out produceType);
				_compCache[charName] = comps;
				_produceCache[charName] = produceType;
			}
			else
			{
				_produceCache.TryGetValue(charName, out produceType);
			}
			if (comps == null || comps.Count == 0)
			{
				return;
			}

			if (comps.Contains("ExplodeComponent")) { outTags.Add("一次性"); }
			if (comps.Contains("AttackComponent")) { outTags.Add("近战"); }
			if (comps.Contains("MousePressComponent") || comps.Contains("CannonComponent")) { outTags.Add("发射"); }
			if (comps.Contains("PeriodicAreaEventComponent")) { outTags.Add("反击"); }
			if (comps.Contains("PotatoComponent")) { outTags.Add("地雷"); }
			if (comps.Contains("MagnetComponent")) { outTags.Add("磁力"); }
			if (comps.Contains("BlockComponent")) { outTags.Add("保护伞"); }
			if (comps.Contains("BloverComponent")) { outTags.Add("吹飞"); }
			if (comps.Contains("SquashComponent")) { outTags.Add("窝瓜"); }
			if (comps.Contains("GrowUpComponent")) { outTags.Add("成长"); }
			if (comps.Contains("TanglekelpComponent")) { outTags.Add("缠绕"); }
			if (comps.Contains("ChomperComponent")) { outTags.Add("大嘴花"); }
			if (comps.Contains("GravebusterComponent")) { outTags.Add("墓吞"); }
			if (comps.Contains("ScaredComponent")) { outTags.Add("胆小"); }
			if (comps.Contains("SlotComponent")) { outTags.Add("花盆"); }
			if (comps.Contains("BowlingComponent")) { outTags.Add("滚动攻击"); }
			if (comps.Contains("MagnetCoinComponent"))
			{
				outTags.Add("吸金");
				outTags.Add("产资源");
			}
			// 「火炬」= 含 ChangeProjectile（表格里写作 ChangeProjectile，
			// 导出里另有一个 ChangeProjectileStateComponent 变体 ⇒ 用前缀匹配）
			foreach (string id in comps)
			{
				if (id.StartsWith("ChangeProjectile", StringComparison.Ordinal))
				{
					outTags.Add("火炬");
					break;
				}
			}
			// 生产：produceType 为 Sun 系 ⇒ 产阳光，其余 ⇒ 产资源
			if (comps.Contains("ProduceComponent") && !string.IsNullOrEmpty(produceType))
			{
				if (produceType == "sun" || produceType == "brainsun"
					|| produceType == "jalasun" || produceType == "qx sun" || produceType == "qxsun"
					|| produceType == "magicsun")
				{
					outTags.Add("产阳光");
				}
				else
				{
					outTags.Add("产资源");
				}
			}
		}
		catch { }
	}

	/// <summary>加载该角色的组件集，返回组件类型 Id 集合 + Produce 的 produceType（小写）。</summary>
	private static HashSet<string> LoadComponentIds(string charName, out string produceType)
	{
		produceType = "";
		var ids = new HashSet<string>();
		try
		{
			ResourceManager rm = ResourceManager.Instance;
			if (rm == null)
			{
				return ids;
			}
			PackedScene scene = rm.GetCharacterScene(charName);
			if (scene == null || !GodotObject.IsInstanceValid(scene))
			{
				return ids;
			}
			string path = scene.ResourcePath;
			if (string.IsNullOrEmpty(path) || !path.EndsWith(".tscn", StringComparison.Ordinal))
			{
				return ids;
			}
			string setPath = path.Substring(0, path.Length - 5) + "ComponentSet.tres";
			if (!ResourceLoader.Exists(setPath))
			{
				return ids;
			}
			Resource res = ResourceLoader.Load(setPath);
			CharacterComponentSet cs = res as CharacterComponentSet;
			if (cs == null)
			{
				return ids;
			}
			// GetFlattenedDefinitions() 会把 ParentSet 链一起摊平（组件集支持继承！）
			IReadOnlyList<CharacterComponentDefinition> defs = cs.GetFlattenedDefinitions();
			if (defs == null)
			{
				return ids;
			}
			for (int i = 0; i < defs.Count; i++)
			{
				CharacterComponentDefinition def = defs[i];
				if (def == null)
				{
					continue;
				}
				string id = def.ComponentTypeId;
				if (string.IsNullOrEmpty(id))
				{
					continue;
				}
				ids.Add(id);
				if (id == "ProduceComponent" && def is ProduceComponentDefinition pcd)
				{
					try
					{
						produceType = (pcd.produceType ?? "").ToLowerInvariant();
					}
					catch { }
				}
			}
		}
		catch { }
		return ids;
	}

	/// <summary>清空组件缓存（换关卡/换卡池时可调用；当前按角色名缓存，跨关卡复用是安全的）。</summary>
	internal static void ClearCache()
	{
		try
		{
			_compCache.Clear();
			_produceCache.Clear();
		}
		catch { }
	}
}
