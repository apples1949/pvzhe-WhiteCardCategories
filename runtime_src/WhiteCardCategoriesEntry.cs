using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「白卡分类」Mod v2 —— 在选卡界面右侧分类栏「僵尸卡」下方新增「白卡分类」按钮，
/// 点开是**二级菜单**：第一级 **5 大类**（攻击 / 生产 / 职能 / 系列 / 属性），
/// 第二级是该大类下的**具体标签**（一次性 / 射击 / 产阳光 / 坚果 / 豌豆 / 夜行 …），
/// 点某标签即把**待选区**切换成带该标签的白卡植物。**只对白卡生效**，**一张卡可带多个标签**。
///
/// ── 数据来源（重要）───────────────────────────────────────────────
/// 标签体系**完全来自《白卡植物标签表.xlsx》**：
///   · 「标签字典」表的**分组列** → 5 大类（字典里的「备注/脚本特化」命中 0，
///     表格说明已并入「其他」，故不设该大类）
///   · 「白卡植物标签」表的**标签列** → 每株植物的标签
/// 构建期由 `gen_whitecard_table.py` 烘焙进
/// <see cref="WhiteCardCategoriesTable"/>（188 株 / 71 标签 / 5 大类）。
///
/// ── 自动识别（为新版本 / 其它 Mod 的白卡兜底）──────────────────────
/// 烘焙表只覆盖制表时的白卡；**游戏更新或 Mod 新增的卡不在表里** ⇒
/// 由 <see cref="WhiteCardAutoTagger"/> 按表格「标签字典」的判定依据
/// （配置字段 + 组件集 + 名称关键词）自动识别标签，仍判不出的进「其他」，**不会漏卡**。
///
/// ── 落点（读源码核实）────────────────────────────────────────────
/// `TowerDefenseInGamePacketBank.tscn`：
///   Translate/CardSort                     ← 右侧分类面板（TextureRect，94 宽）
///     ├ CardMod(y= 62) CardItem(y=137) CardGraveStone(y=212) CardZombie(y=287)
///     └ VBoxContainer/  ← 左列 白卡/金卡/钻卡/彩卡/星卡/原卡
///   每格竖直间距 **75px** ⇒ 「僵尸卡」下面是 **y=362**。
///
/// ── 切换待选区走原生路径 ─────────────────────────────────────────
/// `TowerDefenseBattleFeaturePacketBank.CategoryChoose(标签键)` —— 游戏自己的分类切换函数，
/// 它读 `packetBankData.category[标签键]` 重建卡池 ⇒ 我们只需把自定义标签**注入**
/// `packetBankData.category`（键加 `WCC_` 前缀避免与游戏键撞车），再调用它。
/// ⚠️ `SetPacketBankData()` 每次进关卡都会重建 `packetBankData` ⇒ 注入要按实例 ID 重做。
///
/// ── 铁律 ────────────────────────────────────────────────────────
/// · 手写 csproj 无 Godot 源码生成器 ⇒ 自定义 Node 子类回调不会被调用，逻辑全走
///   `SceneTree.Connect("process_frame", …)` 信号通道。
/// · 初始化三回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · 包内绝不能出现 `.cs`（ModLoader 可执行文件白名单会整包拒收）。
/// </summary>
public sealed class WhiteCardCategoriesEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[WhiteCardCat] ";

	/// <summary>注入到 packetBankData 的标签键前缀（避免与游戏自带键撞车）。</summary>
	private const string KeyPrefix = "WCC_";

	/// <summary>★ v2.14：章节兜底组编码 —— xlsx「获取方式」里没有收录的白卡（游戏更新新增）都归这里。</summary>
	private const string ChapterFallbackCode = "OT";

	/// <summary>诊断日志（默认关；排查时置 true 重新构建）。</summary>
	private static bool EnableLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

	/// <summary>字号选择项（用户要求：16/18/20/22/24/26）。</summary>
	private static readonly int[] FontSizeChoices = { 16, 18, 20, 22, 24, 26 };

	/// <summary>配置持久化文件（Godot user:// = %APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\）。</summary>
	private const string ConfigPath = "user://WhiteCardCategories.cfg";

	/// <summary>当前按钮字号（持久化；默认 18）。</summary>
	private int _fontSize = 18;
		private const string KFollowFont = "followCategoryFontSize";
		private static bool _followFont;
		private const string KDefaultCat = "defaultCategory";
		private string _defaultCat = "";
		private bool _defaultApplied;
		private int _lastFollowFont = -1;

	/// <summary>★ v2.13：两个「是否显示分类按钮」开关（mod.json → 配置页）。</summary>
	private const string KShowBattle = "showInBattleSelect";
	private const string KShowAlmanac = "showInAlmanac";
	private bool _showBattle = true;      // 战斗前选卡界面（含自制关卡编辑器的卡牌库）
	private bool _showAlmanac = true;     // 植物图鉴页

	/// <summary>图鉴「白卡分类」按钮的位置（PlantLayer/Plant 左上角）。</summary>
	private const float AlmanacButtonLeft = 44f;
	private const float AlmanacButtonTop = 34f;    // 卡面素材自身向上溢出约 30px，取 34 才不会被屏幕顶边裁掉

	/// <summary>当前打开的图鉴实例（每次打开都是新节点，关闭即 QueueFree）。</summary>
	private Almanac _almanacNode;

	/// <summary>「白卡分类」按钮在 CardSort 下的位置（与 CardZombie 同列，向下 75px）。</summary>
	private const float ButtonOffsetLeft = 133f;
	private const float ButtonOffsetTop = 362f;

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	/// <summary>已建好 UI 的 packetBank 实例 ID（换关卡会重建）。</summary>
	private ulong _uiBankId;

	private int _frame;

	// ================================================================ 生命周期

	
	// ============================================================ 配置面板接线
	//
	// mod.json 里声明了 enableLog（bool，默认 false）。
	// 这里在 Initialize 读一次 —— 老宿主没有 context.Settings 时吞异常、按 false 处理。
	// 用 Get<object> 而不是 Get<bool>：宿主把值存成 Dictionary<string, object>，
	// 直接 Get<bool> 可能抛 InvalidCastException。

	private static bool ReadLogSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null)
			{
				return false;
			}
			object v = context.Settings.Get<object>("enableLog");
			if (v is bool b)
			{
				return b;
			}
			if (v is string s && bool.TryParse(s, out bool sb))
			{
				return sb;
			}
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return false;
		}
		catch
		{
			return false;
		}
	}


	/// <summary>按「Mod 配置」刷新默认字号（Initialize 与 Changed 都调）。</summary>
	private static void ApplySettings(XWModRuntimeContext context, out int fontSize)
	{
		fontSize = 18;
		try
		{
			if (context == null) { return; }
			object v = context.Settings.Get<object>("defaultFontSize");
			int n = 0;
			if (v is string s) { int.TryParse(s, out n); }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{ int.TryParse(je.GetString(), out n); }
			else { try { n = System.Convert.ToInt32(v); } catch { n = 0; } }
			if (n >= 16 && n <= 26 && (n % 2) == 0) { fontSize = n; }
		}
		catch { }
	}

	/// <summary>★ v2.9.5：订阅标志。</summary>
	private XWModRuntimeContext _context;
	private bool _settingsSubscribedWcc;

	/// <summary>★ v2.9.5：配置保存后重读全部设置（defaultCategory/defaultFontSize/followFont）。</summary>
	private void OnWccSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		try
		{
			ApplySettings(_context, out _fontSize);
			ReadFollowFontSetting(_context);
			ReadDefaultCategorySetting(_context);
			ReadShowSwitches(_context);
			try { ApplyShowSwitches(); } catch { }
			Log("配置已更新，已重读（_defaultCat=" + (_defaultCat == "" ? "不分类" : _defaultCat) + "，字号=" + _fontSize + "）。");
		}
		catch { }
	}

	/// <summary>★ v2.13：读两个「是否显示分类按钮」开关（bool，默认都开）。</summary>
	private void ReadShowSwitches(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			_showBattle = ReadBoolSetting(context, KShowBattle, true);
			_showAlmanac = ReadBoolSetting(context, KShowAlmanac, true);
		}
		catch { }
	}

	/// <summary>从 Mod 配置里取一个 bool（兼容 bool / string / JsonElement 三种存法）。</summary>
	private static bool ReadBoolSetting(XWModRuntimeContext context, string key, bool def)
	{
		try
		{
			if (context == null) { return def; }
			object v = context.Settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je
				&& (je.ValueKind == System.Text.Json.JsonValueKind.True || je.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				return je.GetBoolean();
			}
		}
		catch { }
		return def;
	}

	public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
			EnableLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志（原：EnableLog = ReadLogSetting(context)）

			_context = context;
			ApplySettings(context, out _fontSize);   // 面板里的默认字号覆盖配置文件的初值
			ReadFollowFontSetting(context);   // 是否跟随分类页字号
			ReadDefaultCategorySetting(context);   // 默认显示的分类
			ReadShowSwitches(context);   // ★ v2.13：两个显示开关
			// ★ v2.9.5：订阅配置变化（此前从未订阅 ⇒ 改配置后要重启才生效）
			try
			{
				if (context?.Settings != null && !_settingsSubscribedWcc)
				{
					context.Settings.Changed += OnWccSettingsChanged;
					_settingsSubscribedWcc = true;
				}
			}
			catch { }
		try
		{
			LoadConfig();
			Log("初始化完成（v2 二级菜单）。数据来自《白卡植物标签表.xlsx》：5 大类、"
				+ WhiteCardCategoriesTable.GroupOrder.Length + " 组、"
				+ CountTags() + " 个标签、"
				+ WhiteCardCategoriesTable.PlantTagLines.Length + " 株植物；字号=" + _fontSize + "。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	// ================================================================ 配置

	/// <summary>读配置（字号）。缺文件/缺键时保持默认值，绝不抛。</summary>
	private void LoadConfig()
	{
		try
		{
			ConfigFile cf = new ConfigFile();
			if (cf.Load(ConfigPath) == Error.Ok)
			{
				object v = cf.GetValue("ui", "button_font_size", 18);
				if (v is int iv && IsValidFontSize(iv))
				{
					_fontSize = iv;
				}
			}
		}
		catch { }
	}

	/// <summary>写配置（字号）。失败只记一条日志，不影响游戏内生效。</summary>
	private void SaveConfig()
	{
		try
		{
			ConfigFile cf = new ConfigFile();
			cf.SetValue("ui", "button_font_size", _fontSize);
			cf.Save(ConfigPath);
		}
		catch (Exception ex)
		{
			Log("保存字号失败（本次游戏内仍生效）：" + ex.Message);
		}
	}

	private static bool IsValidFontSize(int v)
	{
		for (int i = 0; i < FontSizeChoices.Length; i++)
		{
			if (FontSizeChoices[i] == v)
			{
				return true;
			}
		}
		return false;
	}

	private static int CountTags()
	{
		int n = 0;
		foreach (KeyValuePair<string, string[]> kv in WhiteCardCategoriesTable.Groups)
		{
			n += kv.Value.Length;
		}
		return n;
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			_started = true;
			Log("已挂载 process_frame。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 每帧

	private void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "白卡分类", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_frame++;

			// ★ v2.13：植物图鉴页。必须放在下面 `TowerDefenseManager.Instance` 判空**之前** ——
			//   图鉴在主菜单 / 暂停菜单里也能打开，那时没有战斗，TowerDefenseManager 可能为空。
			try { TickAlmanac(); } catch { }

			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			TowerDefenseInGamePacketBank bank = feature.packetBank;
			if (bank == null || !GodotObject.IsInstanceValid(bank))
			{
				return;
			}
			Control sort = bank.cardSort;
			if (_followFont) { FollowCategoryFont(sort); }
			if (sort == null || !GodotObject.IsInstanceValid(sort))
			{
				return;
			}

			// ── ① 游戏内选卡界面卡池 ───────────────────────────────
			InjectIntoTarget(feature.packetBankData, _game);
			_game.Choose = key =>
			{
				try
				{
					TowerDefenseBattleFeaturePacketBank f2 = TowerDefenseManager.Instance?.GetPacketBankFeature();
					if (f2 != null && GodotObject.IsInstanceValid(f2))
					{
						f2.CategoryChoose(key);
					}
				}
				catch { }
			};
			if (_uiBankId != bank.GetInstanceId())
			{
				_uiBankId = bank.GetInstanceId();
				_game.Button = null;                     // 换关卡 ⇒ 按钮/下拉都要重建
				_game.Dropdown = null;
				_game.DataId = 0;
				_defaultApplied = false;   // 换关卡 ⇒ 允许重新应用默认分类
			}
			if (!_showBattle)
			{
				// ★ v2.13：开关关着 ⇒ 不建按钮；已建的藏起来（配置热更新立即生效）
				ApplyOneSwitch(_game, false);
				ApplyOneSwitch(_editor, false);
			}
			else if (EnsureButton(_game, sort, "白卡分类", manualPos: true) != null)
			{
				EnsureDropdown(_game, sort);
				ApplyDefaultCategory(_game);   // 默认分类（一次性）
				FlushRePlace(_game);
			}

			// ── ② 自制关卡（关卡编辑器）的编辑卡牌库 ────────────────
			//   那一套卡池是**另一个类** `LevelEditorPacketBank`，
			//   数据来自全局 `PACKETBANKS["Total"]`，同一个"白卡分类"下拉在这里也适用。
			LevelEditorPacketBank eb = LevelEditorPacketBank.Instance;
			if (eb != null && GodotObject.IsInstanceValid(eb)
				&& eb.data != null && GodotObject.IsInstanceValid(eb.data))
			{
				InjectIntoTarget(eb.data, _editor);
				_editor.Choose = key =>
				{
					try
					{
						LevelEditorPacketBank b2 = LevelEditorPacketBank.Instance;
						if (b2 != null && GodotObject.IsInstanceValid(b2))
						{
							b2.CategoryChoose(key);
						}
					}
					catch { }
				};
				Node vb = eb.GetNodeOrNull("VBoxContainer");
				if (!_showBattle)
				{
					ApplyOneSwitch(_editor, false);
				}
				else if (vb != null && EnsureButton(_editor, vb, "白卡分类", manualPos: false) != null)
				{
					EnsureDropdown(_editor, eb);
					ApplyDefaultCategory(_editor);
					FlushRePlace(_editor);
				}
			}
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 标签注入

	/// <summary>烘焙表反查：植物键 → 标签列表（只建一次）。</summary>
	private static Dictionary<string, string[]> _baked;

	private static void EnsureBaked()
	{
		if (_baked != null)
		{
			return;
		}
		_baked = new Dictionary<string, string[]>();
		foreach (string line in WhiteCardCategoriesTable.PlantTagLines)
		{
			int bar = line.IndexOf('|');
			if (bar <= 0)
			{
				continue;
			}
			string key = line.Substring(0, bar);
			string rest = line.Substring(bar + 1);
			_baked[key] = rest.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
		}
	}

	/// <summary>
	/// 把本 Mod 的标签写进当前卡池的 `packetBankData.category`。
	/// 每个标签只保留**当前卡池里真实存在**的白卡（关卡卡池可能是子集）。
	/// `packetBankData` 每次进关卡都会被重建 ⇒ 按实例 ID 判断是否需要重做。
	/// </summary>
	private void InjectIntoTarget(TowerDefensePacketBankData pb, Target t)
	{
		try
		{
			if (pb == null || !GodotObject.IsInstanceValid(pb))
			{
				return;
			}
			if (t.DataId == pb.GetInstanceId() && t.GroupTags != null && t.GroupTags.Count > 0
				&& string.Equals(t.InjectedCat, _defaultCat, StringComparison.Ordinal))
			{
				return;      // 这份卡池数据 + 当前分类模式都已注入过
			}
			// ★ v2.9.2：「不分类」模式 ⇒ 不注入 pb.category（原生下拉不残留），
			//   但 **GroupTags 照常构建**（本 Mod 面板的「一页全部标签」需要它）。
			//   v2.9.1 在这里提前 return 导致 GroupTags 永远为空 ⇒ 面板空壳（用户截图的小点）。
			if (string.IsNullOrEmpty(_defaultCat))
			{
				// 「不分类」：清掉注入键 + 构建 GroupTags（面板数据），但不注入 pb.category
				var wccKeys = new System.Collections.Generic.List<string>();
				foreach (KeyValuePair<Variant, Variant> kv2 in pb.category)
				{
					if (kv2.Key.VariantType == Variant.Type.String
						&& kv2.Key.AsString().StartsWith(KeyPrefix, StringComparison.Ordinal))
					{
						wccKeys.Add(kv2.Key.AsString());
					}
				}
				foreach (string k in wccKeys)
				{
					pb.category.Remove(k);
				}
				Godot.Collections.Array whiteEarly = pb.category.ContainsKey("White")
					? pb.category["White"].AsGodotArray()
					: null;
				var whiteSetEarly = new HashSet<string>();
				if (whiteEarly != null)
				{
					foreach (Variant v in whiteEarly)
					{
						whiteSetEarly.Add(v.AsString());
					}
				}
				EnsureBaked();
				BuildGroupTags(whiteSetEarly, t);
				t.DataId = pb.GetInstanceId();
				t.InjectedCat = _defaultCat;
				return;
			}
			Godot.Collections.Array white = pb.category.ContainsKey("White")
				? pb.category["White"].AsGodotArray()
				: null;
			if (white == null || white.Count == 0)
			{
				return;      // 卡池还没准备好，下一帧再试
			}
			var whiteSet = new HashSet<string>();
			foreach (Variant v in white)
			{
				whiteSet.Add(v.AsString());
			}

			EnsureBaked();

			// ── 逐卡打标签：烘焙表 ∪ 运行时自动识别；都没命中 ⇒ 「其他」兜底 ──
			var tagBuckets = new Dictionary<string, Godot.Collections.Array>();
			int otherCount = 0;
			int autoHits = 0;
			foreach (string k in whiteSet)
			{
				var tags = new HashSet<string>();
				bool inBaked = _baked.TryGetValue(k, out string[] bakedTags);
				if (inBaked)
				{
					foreach (string tg in bakedTags)
					{
						tags.Add(tg);
					}
				}
				else
				{
					// ★ 只对**表外**的卡跑自动识别（游戏更新 / 其它 Mod 新增的白卡）。
					//   为什么不对全部卡跑：自动识别要加载该角色的**组件集资源**，
					//   188 张卡全跑一遍会在进选卡界面时集中做 188 次资源加载 ⇒ 明显卡顿；
					//   而烘焙表本来就是这 188 株的权威快照，再跑一遍是白做功。
					//   （若游戏更新改了**已有**卡的标签，请重新导出表格并重生成
					//     `WhiteCardCategoriesTable.cs`，而不是依赖这里的兜底。）
					try
					{
						TowerDefensePacketConfig cfg = TowerDefenseManager.GetPacketConfig(k);
						if (cfg != null && GodotObject.IsInstanceValid(cfg)
							&& cfg.characterConfig is TowerDefensePlantConfig pc)
						{
							WhiteCardAutoTagger.Tag(k, pc, tags);
							autoHits++;
						}
					}
					catch { }
				}

				if (tags.Count == 0)
				{
					tags.Add("其他");
				}
				foreach (string tg in tags)
				{
					if (!tagBuckets.TryGetValue(tg, out Godot.Collections.Array arr))
					{
						arr = new Godot.Collections.Array();
						tagBuckets[tg] = arr;
					}
					arr.Add(k);
					if (tg == "其他")
					{
						otherCount++;
					}
				}
			}


	// 写入 packetBankData（键加前缀避免与游戏键撞车）
	foreach (KeyValuePair<string, Godot.Collections.Array> kv in tagBuckets)
	{
		if (kv.Value.Count > 0)
		{
			pb.category[KeyPrefix + kv.Key] = kv.Value;
		}
	}

	// ★ v2.14：注入「按获取章节」分组（WCC_Chapter_<组码> → 该组白卡列表）
	//   数据源从 .tres 的 levelSaveKey 换成 xlsx「图鉴说明」的「获取方式」文本，
	//   覆盖 通关冒险模式 X-Y / 在线关卡水晶兑换 / 商店购买 / 杂交乐园 / 抽奖盒子 / 初始可用。
	EnsureBaked();
	var chapterBuckets = new Dictionary<string, Godot.Collections.Array>();
	foreach (string k in whiteSet)
	{
		string code = WhiteCardChapterSources.ChapterOf(k);
		if (string.IsNullOrEmpty(code)) { code = ChapterFallbackCode; }   // 表外新卡 ⇒ 兜底「其他」
		if (!chapterBuckets.TryGetValue(code, out Godot.Collections.Array arrL))
		{
			arrL = new Godot.Collections.Array();
			chapterBuckets[code] = arrL;
		}
		arrL.Add(k);
	}
	foreach (KeyValuePair<string, Godot.Collections.Array> kv in chapterBuckets)
	{
		pb.category[KeyPrefix + "Chapter_" + kv.Key] = kv.Value;
	}

	// ── 组装「大类 → 非空标签」，供二级菜单使用（空标签不显示）──
	var groupTags = new Dictionary<string, List<string>>();
	int shown = 0;
	foreach (string grp in WhiteCardCategoriesTable.GroupOrder)
	{
		if (!WhiteCardCategoriesTable.Groups.TryGetValue(grp, out string[] tagsOfGroup))
		{
			continue;
		}
		var list = new List<string>();
		foreach (string tg in tagsOfGroup)
		{
			if (tagBuckets.TryGetValue(tg, out Godot.Collections.Array arr) && arr.Count > 0)
			{
				list.Add(tg);
				shown++;
			}
		}
		if (list.Count > 0)
		{
			groupTags[grp] = list;
		}
	}
	t.GroupTags = groupTags;
	t.OtherCount = otherCount;
	t.DataId = pb.GetInstanceId();
		t.InjectedCat = _defaultCat;
	RebuildDropdown(t);      // 卡池变了 ⇒ 菜单内容跟着刷新（回到第一级）

	Log("已注入白卡标签：" + groupTags.Count + " 大类 / " + shown + " 个非空标签 / "
		+ whiteSet.Count + " 张白卡（其中表外自动识别 " + autoHits + " 张）；"
		+ "兜底「其他」" + otherCount + " 张。");
}
		catch (Exception ex)
		{
			if (_diag < 101)
			{
				_diag = 101;
				Log("标签注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}
	/// <summary>★ v2.9.2：「不分类」模式的数据构建——逐卡打标签 + 组装 GroupTags/OtherCount，
	/// 但【不】注入 pb.category（原生下拉不残留）、【不】RebuildDropdown（首屏由 ToggleFor 决定）。</summary>
	private void BuildGroupTags(HashSet<string> whiteSet, Target t)
	{
		var tagBuckets = new Dictionary<string, Godot.Collections.Array>();
		int otherCount = 0;
		foreach (string k in whiteSet)
		{
			var tags = new HashSet<string>();
			if (_baked.TryGetValue(k, out string[] bakedTags))
			{
				foreach (string tg in bakedTags) { tags.Add(tg); }
			}
			else
			{
				try
				{
					TowerDefensePacketConfig cfg = TowerDefenseManager.GetPacketConfig(k);
					if (cfg != null && GodotObject.IsInstanceValid(cfg)
						&& cfg.characterConfig is TowerDefensePlantConfig pc)
					{
						WhiteCardAutoTagger.Tag(k, pc, tags);
					}
				}
				catch { }
			}
			if (tags.Count == 0) { tags.Add("其他"); }
			foreach (string tg in tags)
			{
				if (!tagBuckets.TryGetValue(tg, out Godot.Collections.Array arr))
				{
					arr = new Godot.Collections.Array();
					tagBuckets[tg] = arr;
				}
				arr.Add(k);
				if (tg == "其他") { otherCount++; }
			}
		}
		var groupTags = new Dictionary<string, List<string>>();
		foreach (string grp in WhiteCardCategoriesTable.GroupOrder)
		{
			if (!WhiteCardCategoriesTable.Groups.TryGetValue(grp, out string[] tagsOfGroup))
			{
				continue;
			}
			var list = new List<string>();
			foreach (string tg in tagsOfGroup)
			{
				if (tagBuckets.TryGetValue(tg, out Godot.Collections.Array arr) && arr.Count > 0)
				{
					list.Add(tg);
				}
			}
			if (list.Count > 0) { groupTags[grp] = list; }
		}
		t.GroupTags = groupTags;
		t.OtherCount = otherCount;
	}


	// ================================================================ 建 UI

	/// <summary>
	/// 一套"卡池目标"的 UI 状态 —— 游戏内选卡界面与**自制关卡的编辑卡牌库**
	/// 各持一份，代码完全共用。
	/// </summary>
	private sealed class Target
	{
		public ulong DataId;                            // 已注入的那份卡池数据（实例 ID）
		/// <summary>★ v2.12.2：注入时所用的分类模式（_defaultCat）。模式变了必须重新注入 ——
		/// 否则「不分类」跑过一次后切回普通分类时，守卫会因 DataId 相同而跳过，
		/// 导致 pb.category 永远没有 WCC_ 键、点标签查不到 ⇒ 不出植物。</summary>
		public string InjectedCat;
		public Dictionary<string, List<string>> GroupTags;  // 大类 → 非空标签（空大类不放）
		public int OtherCount;                           // 「其他」里的卡数
		public PacketCategoryButton Button;              // 「白卡分类」按钮
		public Control Dropdown;                         // 自绘二级下拉面板
		public Control Parent;                           // 下拉面板挂载的父节点（重建面板时要用）
		public int PlaceFrames;                          // 还需重贴几帧（容器最小尺寸是延迟算的）
		public Action<string> Choose;                    // 点标签后的切换回调
		public PacketCategoryButton.ChooseEventHandler ChooseHandler;  // ★ v2.8：固定桥接委托（订阅一次永不移除）
		// ── ★ v2.13：仅「图鉴」用 —— 图鉴没有 CategoryChoose，改成本地过滤 ──
		public Almanac AlmanacNode;                    // 所属图鉴实例
		public HashSet<string> AlmanacFilter;          // null = 不筛选；非 null = 只显示集合内的植物键
	}

	private readonly Target _game = new Target();     // 游戏内选卡界面
	private readonly Target _editor = new Target();   // 自制关卡的编辑卡牌库
	private readonly Target _almanac = new Target();  // ★ v2.13：植物图鉴页

	// ── ★ v2.13：图鉴筛选的临时状态 ──
	//   做法：**不动** `Almanac.plantCategoryId`（翻页语义 / 页数完全不变），
	//   只把「当前页」那个 category 键的数组临时换成筛选结果；
	//   取消筛选或翻页时把原数组写回去。
	//   为什么不去改 `_plantLogicalConfigs`（图鉴真正的数据源）：那是 private，
	//   而 `InitPlant()` 就是从 `plantPacketBank.category[plantCategoryId]` 重算它的
	//   ⇒ 换掉那个键的内容 = 官方认可的入口，不用反射、不用 Harmony。
	private bool _almanacFilterActive;
	private Variant _almanacFilterKey;                    // 被临时替换的 category 键
	private Godot.Collections.Array _almanacFilterBackup; // 该键原本的数组
	private int _almanacLastCatId = -1;                   // 上一帧的 plantCategoryId（翻页检测）

	/// <summary>
	/// 确保「白卡分类」按钮存在（实例化游戏官方按钮场景，外观与其它分类按钮一致）。
	/// </summary>
	private PacketCategoryButton EnsureButton(Target t, Node parent, string label, bool manualPos)
	{
		try
		{
			if (t.Button != null && GodotObject.IsInstanceValid(t.Button) && t.Button.IsInsideTree())
			{
				return t.Button;
			}
			if (parent == null || !GodotObject.IsInstanceValid(parent))
			{
				return null;
			}
			PacketCategoryButton btn = InstantiateCategoryButton();
			if (btn == null)
			{
				return null;
			}
			btn.Name = "WCC_Button";
			parent.AddChild(btn, false, Node.InternalMode.Disabled);
			Label lb = btn.GetNodeOrNull<Label>("LabelNode/Label");
			if (lb != null && GodotObject.IsInstanceValid(lb))
			{
				lb.Text = label;
			}
			// ⚠️ `PacketCategoryButton` **没有** `Pressed`；它的点击链是
			//   `SpriteBrightButton.OnPressed → ButtonPressed() → OnChoose?.Invoke(category)`
			//   ⇒ 订阅 `OnChoose`（委托类型 `ChooseEventHandler(string)`），
			//     忽略它传来的游戏自带 category，改成切换我们自己的二级下拉。
			// ★ v2.9：固定订阅 ToggleFor（点击弹本 Mod 面板）；「默认分类」只决定
			//   进关后首次显示哪一页（全部标签一页铺开 / 5 大类分组），见 ApplyDefaultCategory。
			t.ChooseHandler = _ => ToggleFor(t);
			btn.OnChoose += t.ChooseHandler;
			if (manualPos)
			{
				// 与 CardZombie 同列、向下 75px（实测间距）
				btn.Position = new Vector2(ButtonOffsetLeft, ButtonOffsetTop);
			}
			t.Button = btn;
			Log("已创建「" + label + "」按钮（manualPos=" + manualPos + "）。");
			return btn;
		}
		catch (Exception ex)
		{
			if (_diag < 102)
			{
				_diag = 102;
				Log("建按钮异常（本条只报一次）：" + ex.Message);
			}
			return null;
		}
	}

	/// <summary>实例化游戏官方的分类按钮场景（拿不到就返回 null）。</summary>
	private static PacketCategoryButton InstantiateCategoryButton()
	{
		try
		{
			PackedScene sc = GD.Load<PackedScene>(
				"res://Registry/Battle/Feature/PacketBank/PacketBank/PacketCategory/PacketCategoryButton.tscn");
			if (sc != null && GodotObject.IsInstanceValid(sc))
			{
				return sc.Instantiate<PacketCategoryButton>(PackedScene.GenEditState.Disabled);
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 建（或重建）二级下拉面板。
	/// 第一级 = 5 大类（+「其他」）；点大类 ⇒ 第二级 = 该大类的标签（+「← 返回」）。
	/// </summary>
	private void EnsureDropdown(Target t, Control parent)
	{
		try
		{
			t.Parent = parent;
			if (t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown) && t.Dropdown.IsInsideTree())
			{
				return;
			}
			BuildPanel(t);
		}
		catch (Exception ex)
		{
			if (_diag < 104)
			{
				_diag = 104;
				Log("建下拉异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// ★ **每次都重建一个全新的面板节点**（而不是清空内容复用）。
	///
	/// 为什么必须重建：Godot 的 `Control.size` **只会涨到够用，不会因内容变小而自动缩回**
	/// （`size = max(锚点推出的期望尺寸, 最小尺寸)`，期望尺寸恒为 0，所以它等于最小尺寸；
	///  但最小尺寸的更新依赖容器 `queue_sort()` 的**延迟**重算，
	///  同帧内 `ResetSize()` 拿到的仍是旧值）。
	/// 于是从「显示所有标签」（64 项 / 6 列 11 行）返回第一级（6 项 / 2 列 3 行）时，
	/// 面板会**保持之前的大尺寸** ⇒ 按钮下方留一大片空白（用户截图里的异常就是它）。
	/// 直接换一个新面板：尺寸从 0 按新内容重算，天然没有这个问题。
	///
	/// 可见性沿用旧面板（首次创建时为 false）。
	/// </summary>
	private void BuildPanel(Target t)
	{
		try
		{
			bool wasVisible = false;
			if (t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				wasVisible = t.Dropdown.Visible;
				t.Dropdown.Visible = false;      // 先藏起来，避免同帧看到新旧两个面板
				t.Dropdown.QueueFree();
				t.Dropdown = null;
			}
			if (t.Parent == null || !GodotObject.IsInstanceValid(t.Parent))
			{
				return;
			}

			PanelContainer panel = new PanelContainer();
			panel.Name = "WCC_Dropdown";
			panel.Visible = wasVisible;
			// 脱离父级绘制顺序与裁剪（否则会被卡池面板盖住）
			panel.TopLevel = true;
			panel.ZIndex = 200;

			StyleBoxFlat sb = new StyleBoxFlat();
			sb.BgColor = new Color(0.16f, 0.12f, 0.07f, 0.96f);
			sb.BorderColor = new Color(0.55f, 0.42f, 0.22f, 1f);
			sb.SetBorderWidthAll(2);
			sb.SetCornerRadiusAll(8);
			sb.ContentMarginLeft = 8f;
			sb.ContentMarginRight = 8f;
			sb.ContentMarginTop = 8f;
			sb.ContentMarginBottom = 8f;
			panel.AddThemeStyleboxOverride("panel", sb);

			// 结构：PanelContainer → VBoxContainer「WCC_Box」→ [返回按钮] + GridContainer「WCC_Grid」
			//   ★ 返回按钮**放在网格外**：它的文字较长（"← 显示标签分类"），
			//     若放进 GridContainer，GridContainer 会按最宽子项把**所有列**一起撑宽，
			//     标签之间就会留下大片空白。放 VBox 里它独占一行、网格列宽保持均匀。
			VBoxContainer box = new VBoxContainer();
			box.Name = "WCC_Box";
			box.AddThemeConstantOverride("separation", 6);
			panel.AddChild(box);

			GridContainer grid = new GridContainer();
			grid.Name = "WCC_Grid";
			grid.AddThemeConstantOverride("h_separation", 8);
			grid.AddThemeConstantOverride("v_separation", 6);
			box.AddChild(grid);

			t.Parent.AddChild(panel);
			t.Dropdown = panel;
			// ★ 容器的最小尺寸是**延迟**算的（`queue_sort()`）⇒ 同帧 `PlaceFor` 可能拿到偏小的尺寸、
			//   把面板贴得偏低甚至越界。这里标记"接下来几帧各重贴一次"，让它稳定下来。
			t.PlaceFrames = 3;
		}
		catch { }
	}

	/// <summary>取面板里的按钮网格（结构：PanelContainer → VBox「WCC_Box」→ Grid「WCC_Grid」）。</summary>
	private static GridContainer GetGrid(Target t)
	{
		try
		{
			return t?.Dropdown?.GetNodeOrNull<GridContainer>("WCC_Box/WCC_Grid");
		}
		catch { return null; }
	}

	/// <summary>按钮字号换算出的最小高度（含内容边距）。</summary>
	private int BtnMinH()
	{
		return _fontSize + 20;
	}

	/// <summary>按钮字号换算出的最小宽度（够放 4 个汉字 + 左右边距）。</summary>
	private int BtnMinW()
	{
		return _fontSize * 4 + 24;
	}

	/// <summary>在面板顶部放一个整行宽的返回/切换按钮（放在网格外，不撑歪列宽）。</summary>
	private void SetBack(Target t, string label, Action act)
	{
		try
		{
			Node box = t?.Dropdown?.GetNodeOrNull("WCC_Box");
			if (box == null || !GodotObject.IsInstanceValid(box))
			{
				return;
			}
			Button b = MakeButton(label);
			b.Name = "WCC_Back";
			b.CustomMinimumSize = new Vector2(0f, BtnMinH());      // 整行宽（VBox 里横向填满）
			if (act != null)
			{
				b.Pressed += () => act();
			}
			box.AddChild(b);
			box.MoveChild(b, 0);      // 永远排在最上面
		}
		catch { }
	}

	/// <summary>造一个统一样式的按钮（★ 字号取 <see cref="_fontSize"/>，尺寸随字号缩放）。</summary>
	/// <summary>读"是否跟随分类页字号"设置（bool，默认 false）。</summary>
	/// <summary>读「默认显示的分类」设置（string：none 或 攻击/生产/职能/系列/属性）。</summary>
	private void ReadDefaultCategorySetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			object v = context.Settings.Get<object>(KDefaultCat);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String) { s = je.GetString(); }
			if (string.IsNullOrWhiteSpace(s)) { return; }
			s = s.Trim();
			_defaultCat = s.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : s;
			_defaultApplied = false;   // 改了设置 ⇒ 允许重新应用
		}
		catch { }
	}

	/// <summary>
	/// 应用「默认分类」：只在**每个关卡/每次重建后应用一次**（`_defaultApplied` 一次性标志）。
	/// ⚠️ 绝不能每帧应用 —— 否则会不断重置玩家自己的选择（会表现为"选什么都跳回默认"）。
	/// ★ v2.4.1：`_defaultCat` 为空（「不分类」）⇒ 图鉴按钮恢复**游戏原生行为**
	///   （OnChoose → TowerDefenseBattleFeaturePacketBank.CategoryChoose(btn.category)），
	///   同时隐藏本 Mod 的二级下拉面板。
	/// </summary>
	private void ApplyDefaultCategory(Target t)
	{
		try
		{
			if (_defaultApplied || t == null) { return; }
			_defaultApplied = true;
			// ★ v2.9.1：首次显示哪一页改由 ToggleFor（点按钮时刻）按 _defaultCat 决定，
			//   这里只做标记，避免 InjectIntoTarget 同帧的 RebuildDropdown 覆盖。
			_defaultApplied = true;
		}
		catch { }
	}


	/// <summary>本 Mod 分类模式的点击处理（= 原来的 ToggleFor）。
	/// ★ v2.4.2：通过 category 字符串判定不靠谱（原生也传 category），
	///   改由按钮实例归属：OnChoose 的 sender 没有参数 ⇒ 用点击源 = 分类按钮本身，
	///   我们在两处（_game/_editor）各有一个按钮 ⇒ 按 Target.Button 的 InstanceId 匹配。
	///   简化：HandleChooseForMod 由按钮闭包直接传 Target 进来（见 EnsureButton）。</summary>
	private void HandleChooseForMod(Target t)
	{
		ToggleFor(t);
	}

	private static object GetSingletonM(string typeName)
	{
		try
		{
			foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(typeName, false); } catch { }
				if (t == null) { continue; }
				System.Reflection.PropertyInfo p = t.GetProperty("Instance",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
				if (p != null) { return p.GetValue(null); }
				System.Reflection.FieldInfo f = t.GetField("Instance",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
				if (f != null) { return f.GetValue(null); }
			}
		}
		catch { }
		return null;
	}

	private static object GetMemberM(object target, string name)
	{
		if (target == null) { return null; }
		try
		{
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				System.Reflection.FieldInfo f = t.GetField(name,
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
				if (f != null) { return f.GetValue(target); }
			}
			for (Type t = target.GetType(); t != null; t = t.BaseType)
			{
				System.Reflection.PropertyInfo p = t.GetProperty(name,
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
				if (p != null && p.CanRead) { return p.GetValue(target); }
			}
		}
		catch { }
		return null;
	}

	private void ReadFollowFontSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null) { return; }
			object v = context.Settings.Get<object>(KFollowFont);
			if (v is bool b) { _followFont = b; }
			else if (v is string s && bool.TryParse(s, out bool sb)) { _followFont = sb; }
			else if (v is System.Text.Json.JsonElement je
				&& (je.ValueKind == System.Text.Json.JsonValueKind.True || je.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				_followFont = je.GetBoolean();
			}
		}
		catch { }
	}

	/// <summary>
	/// 跟随分类页字号：从游戏右侧分类面板（`bank.cardSort`）里找一个 Label，读它的字号。
	/// ⚠️ 找到就更新 `_fontSize` 并同步到已有的 `WCC_Button`（**只改字号，不重建**）；
	///    按钮尺寸随字号缩放（`_fontSize + 20` 等），下次进入选卡界面重建时会用新尺寸。
	/// </summary>
	private void FollowCategoryFont(Control sort)
	{
		try
		{
			if (sort == null || !GodotObject.IsInstanceValid(sort)) { return; }
			Label gameLabel = FindFirstLabel(sort, 0);
			if (gameLabel == null) { return; }
			int fs = gameLabel.GetThemeFontSize("font_size");
			if (fs < 8 || fs > 72) { return; }          // 异常值不采信
			if (fs == _lastFollowFont && fs == _fontSize) { return; }
			_lastFollowFont = fs;
			if (fs == _fontSize) { return; }
			_fontSize = fs;
			// 同步到已有按钮（只改字号）
			Node btn = sort.GetNodeOrNull("WCC_Button");
			if (btn is Button bb && GodotObject.IsInstanceValid(bb))
			{
				bb.AddThemeFontSizeOverride("font_size", _fontSize);
			}
			Log("跟随分类页字号 → " + _fontSize);   // Log 内部已受 EnableLog 门控
		}
		catch { }
	}

	/// <summary>在子树里找第一个 Label（按深度优先，最多下探 4 层）。</summary>
	private static Label FindFirstLabel(Node root, int depth)
	{
		try
		{
			if (root == null || !GodotObject.IsInstanceValid(root) || depth > 4) { return null; }
			foreach (Node c in root.GetChildren())
			{
				if (c is Label l && GodotObject.IsInstanceValid(l)) { return l; }
				Label r = FindFirstLabel(c, depth + 1);
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}

	private Button MakeButton(string text)
	{
		Button b = new Button();
		b.Text = text;
		b.CustomMinimumSize = new Vector2(BtnMinW(), BtnMinH());
		b.Alignment = HorizontalAlignment.Center;
		b.AddThemeFontSizeOverride("font_size", _fontSize);

		// 显式给内容边距：否则按钮高度完全由主题的垂直边距决定（实测每行高达约 58px）。
		StyleBoxFlat normal = new StyleBoxFlat();
		normal.BgColor = new Color(0.86f, 0.78f, 0.58f, 1f);
		normal.BorderColor = new Color(0.42f, 0.31f, 0.15f, 1f);
		normal.SetBorderWidthAll(2);
		normal.SetCornerRadiusAll(6);
		normal.ContentMarginTop = 7f;
		normal.ContentMarginBottom = 7f;
		normal.ContentMarginLeft = 10f;
		normal.ContentMarginRight = 10f;
		b.AddThemeStyleboxOverride("normal", normal);

		StyleBoxFlat hover = new StyleBoxFlat();
		hover.BgColor = new Color(0.97f, 0.90f, 0.68f, 1f);
		hover.BorderColor = new Color(0.42f, 0.31f, 0.15f, 1f);
		hover.SetBorderWidthAll(2);
		hover.SetCornerRadiusAll(6);
		// 与 normal 用同一套边距，避免 hover 时按钮跳动
		hover.ContentMarginTop = 7f;
		hover.ContentMarginBottom = 7f;
		hover.ContentMarginLeft = 10f;
		hover.ContentMarginRight = 10f;
		b.AddThemeStyleboxOverride("hover", hover);
		b.AddThemeStyleboxOverride("pressed", hover);
		b.AddThemeColorOverride("font_color", new Color(0.20f, 0.13f, 0.05f));
		return b;
	}

	/// <summary>当前视口尺寸（拿不到就退回 1920×1080）。</summary>
	private Vector2 ViewportSize()
	{
		try
		{
			Vector2 vs = _tree.Root.GetVisibleRect().Size;
			if (vs.X > 1f && vs.Y > 1f)
			{
				return vs;
			}
		}
		catch { }
		return new Vector2(1920f, 1080f);
	}

	/// <summary>
	/// 第一级：5 大类 +「显示所有标签」（+ 有落单卡时的「其他」）。
	/// </summary>
	private void RebuildDropdown(Target t)
	{
		try
		{
			BuildPanel(t);      // ★ 换层一律重建面板（根除尺寸不回缩）
			GridContainer grid = GetGrid(t);
			if (grid == null)
			{
				return;
			}

			var items = new List<(string Text, Action Act)>();
			bool hasAny = false;
			if (t.GroupTags != null && t.GroupTags.Count > 0)
			{
				foreach (string grp in WhiteCardCategoriesTable.GroupOrder)
				{
					if (!t.GroupTags.TryGetValue(grp, out List<string> tags) || tags.Count == 0)
					{
						continue;
					}
					hasAny = true;
					string g = grp;
					int n = tags.Count;
					items.Add((g + "（" + n + "）", () => ShowTagsOf(t, g)));
				}
			}
			if (hasAny)
			{
				// 用户要求：第一级加一项「显示所有标签」，一次铺开全部标签
				items.Add(("显示所有标签", () => ShowAllTags(t)));
			}
			if (t.OtherCount > 0)
			{
				items.Add(("其他（" + t.OtherCount + "）", () => OnCategoryClicked(t, "其他")));
			}
			// ★ v2.13：图鉴侧正在筛选 ⇒ 给一个明确的「取消筛选」出口
			if (ReferenceEquals(t, _almanac) && _almanacFilterActive)
			{
				items.Add(("✕ 取消筛选", () => ClearAlmanacFilter(t)));
			}
			// ★ v2.14：第一级「按获取章节」入口（二级列出来源分组，点分组筛该组的白卡）
			items.Add(("按获取章节", () => ShowLevelSourcePage(t)));
			FillGrid(grid, items, cols: 2);
			Resize(t, items.Count, 2);
			AddFontRow(t);      // ★ 第一级末尾追加一行「字号」选择
		}
		catch { }
	}


	/// <summary>★ v2.14：「按获取章节」二级页——按【获取方式】分组（xlsx 图鉴说明），点分组筛该组白卡。
	/// 分组清单由 <see cref="WhiteCardChapterSources.ChapterGroupLines"/> 决定（15 组，188 株全覆盖，
	/// 不再有「其他」兜底）。</summary>
	private void ShowLevelSourcePage(Target t)
	{
		try
		{
			BuildPanel(t);
			GridContainer grid = GetGrid(t);
			if (grid == null)
			{
				return;
			}
			SetBack(t, "← 返回", () => RebuildDropdown(t));
			// ★ v2.14：按 xlsx「获取方式」的新章节清单分组（15 组，188 株全部有归属）
			var byGroup = new Dictionary<string, List<string>>();
			foreach (string line in WhiteCardChapterSources.PlantChapterLines)
			{
				int i = line.IndexOf('|');
				if (i <= 0) { continue; }
				string plantKey = line.Substring(0, i);
				string code = line.Substring(i + 1);
				if (code.Length == 0) { continue; }
				if (!byGroup.TryGetValue(code, out List<string> list2))
				{
					list2 = new List<string>();
					byGroup[code] = list2;
				}
				list2.Add(plantKey);
			}
			var items2 = new List<(string Text, Action Act)>();
			foreach (string code in WhiteCardChapterSources.OrderedCodes)
			{
				if (!byGroup.TryGetValue(code, out List<string> keys) || keys.Count == 0) { continue; }
				string label = WhiteCardChapterSources.LabelOf(code) + "（" + keys.Count + "）";
				string cap = code;
				List<string> capKeys = keys;
				items2.Add((label, () => OnChapterClicked(t, cap, capKeys)));
			}
			// 兜底组：「其他」= xlsx 表外的新白卡（游戏更新 / 其它 Mod 新增），永远排在最后
			if (byGroup.TryGetValue(ChapterFallbackCode, out List<string> fbKeys) && fbKeys.Count > 0)
			{
				items2.Add(("其他（" + fbKeys.Count + "）",
					() => OnChapterClicked(t, ChapterFallbackCode, fbKeys)));
			}
			FillGrid(grid, items2, cols: 2);
			Resize(t, items2.Count, 2);
		}
		catch { }
	}


	/// <summary>★ v2.14：点了某个来源分组 ⇒ 把该组白卡列表写入 pb.category（WCC_Chapter_&lt;组码&gt;）并切换。
	/// pb 从 TowerDefenseManager.Instance 取（Resource，非 Node）。</summary>
	private void OnChapterClicked(Target t, string groupCode, List<string> plantKeys)
	{
		try
		{
			string groupLabel = groupCode == ChapterFallbackCode
				? "其他"
				: WhiteCardChapterSources.LabelOf(groupCode);
			// ★ v2.13：图鉴侧走本地筛选，不写战斗卡池
			if (ReferenceEquals(t, _almanac))
			{
				FilterAlmanacPage(t, new HashSet<string>(plantKeys ?? new List<string>()), groupLabel);
				return;
			}
			string key = KeyPrefix + "Chapter_" + groupCode;
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			TowerDefensePacketBankData pb = mgr?.GetPacketBankFeature()?.packetBankData;
			if (pb != null && GodotObject.IsInstanceValid(pb))
			{
				var arr = new Godot.Collections.Array();
				foreach (string k in plantKeys) { arr.Add(k); }
				pb.category[key] = arr;
			}
			if (t.Choose != null)
			{
				t.Choose(key);
			}
			Log("已切换到「" + groupLabel + "」（" + plantKeys.Count + " 株）。");
		}
		catch { }
	}

	/// <summary>
	/// 在**第一级**面板底部加一行字号选择（16/18/20/22/24/26）。
	/// 放在网格外的 VBox 里（单独一行），当前字号用高亮底色标出。
	/// </summary>
	private void AddFontRow(Target t)
	{
		try
		{
			Node box = t?.Dropdown?.GetNodeOrNull("WCC_Box");
			if (box == null || !GodotObject.IsInstanceValid(box))
			{
				return;
			}
			HBoxContainer row = new HBoxContainer();
			row.Name = "WCC_FontRow";
			row.AddThemeConstantOverride("separation", 6);
			row.Alignment = BoxContainer.AlignmentMode.Center;

			Label lb = new Label();
			lb.Text = "字号";
			lb.AddThemeFontSizeOverride("font_size", 16);
			lb.AddThemeColorOverride("font_color", new Color(0.92f, 0.88f, 0.76f));
			row.AddChild(lb);

			foreach (int fs in FontSizeChoices)
			{
				int size = fs;
				Button b = MakeButton(size.ToString());
				// 字号按钮窄一点即可（2 位数）
				b.CustomMinimumSize = new Vector2(48f, BtnMinH());
				b.AddThemeFontSizeOverride("font_size", 16);
				if (size == _fontSize)
				{
					// 当前字号：换成醒目的高亮底色，一眼能看出选中的是哪个
					StyleBoxFlat cur = new StyleBoxFlat();
					cur.BgColor = new Color(0.42f, 0.72f, 0.36f, 1f);
					cur.BorderColor = new Color(0.18f, 0.40f, 0.14f, 1f);
					cur.SetBorderWidthAll(2);
					cur.SetCornerRadiusAll(6);
					cur.ContentMarginTop = 7f;
					cur.ContentMarginBottom = 7f;
					cur.ContentMarginLeft = 10f;
					cur.ContentMarginRight = 10f;
					b.AddThemeStyleboxOverride("normal", cur);
					b.AddThemeStyleboxOverride("hover", cur);
					b.AddThemeStyleboxOverride("pressed", cur);
					b.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
				}
				b.Pressed += () => SetFontSize(t, size);
				row.AddChild(b);
			}

			box.AddChild(row);
			// 面板高度变了 ⇒ 重新贴位（AddFontRow 后没有 Resize 调用，这里补一次）
			t.PlaceFrames = 3;
		}
		catch { }
	}

	/// <summary>用户点了某个字号 ⇒ 记住 + 重建面板（所有按钮立刻用新字号）。</summary>
	private void SetFontSize(Target t, int size)
	{
		try
		{
			if (size == _fontSize)
			{
				return;
			}
			_fontSize = size;
			SaveConfig();
			Log("按钮字号 -> " + size);
			// 重建面板：所有按钮用新字号，尺寸/列数重算（BuildPanel 会沿用可见性）
			RebuildDropdown(t);
		}
		catch { }
	}

	/// <summary>第二级（某个大类）：该大类下的标签；首行「← 返回」回第一级。</summary>
	private void ShowTagsOf(Target t, string group)
	{
		try
		{
			if (t.GroupTags == null || !t.GroupTags.TryGetValue(group, out List<string> tags))
			{
				return;
			}
			BuildPanel(t);
			GridContainer grid = GetGrid(t);
			if (grid == null)
			{
				return;
			}
			SetBack(t, "← 返回", () => RebuildDropdown(t));
			var items = new List<(string Text, Action Act)>();
			foreach (string tg in tags)
			{
				string tag = tg;
				items.Add((tag, () => OnCategoryClicked(t, tag)));
			}
			// 标签名普遍 2~4 字，3 列紧凑
			FillGrid(grid, items, cols: 3);
			Resize(t, items.Count, 3);
		}
		catch { }
	}

	/// <summary>
	/// 「显示所有标签」页：把 5 大类下的**全部非空标签**铺在一页里；
	/// 首行给「← 显示标签分类」入口回到第一级（用户要求）。
	/// </summary>
	private void ShowAllTags(Target t)
	{
		try
		{
			if (t.GroupTags == null)
			{
				return;
			}
			BuildPanel(t);
			GridContainer grid = GetGrid(t);
			if (grid == null)
			{
				return;
			}
			SetBack(t, "← 显示标签分类", () => RebuildDropdown(t));
			var items = new List<(string Text, Action Act)>();
			// ★ v2.13：图鉴侧正在筛选 ⇒ 首选项给「取消筛选」
			if (ReferenceEquals(t, _almanac) && _almanacFilterActive)
			{
				items.Add(("✕ 取消筛选", () => ClearAlmanacFilter(t)));
			}
			foreach (string grp in WhiteCardCategoriesTable.GroupOrder)
			{
				if (!t.GroupTags.TryGetValue(grp, out List<string> tags) || tags.Count == 0)
				{
					continue;
				}
				foreach (string tg in tags)
				{
					string tag = tg;
					items.Add((tag, () => OnCategoryClicked(t, tag)));
				}
			}
			// ★ 列数**按视口自适应**（不写死）：
			//   先给 6 列（够紧凑），再按视口**宽度**上限收窄；
			//   若仍过高（屏幕很矮），在宽度允许范围内继续加列。
			Vector2 vp = ViewportSize();
			float colPitch = BtnMinW() + 8f;   // 每格宽 + h_separation
			float rowPitch = BtnMinH() + 6f;   // 每行高 + v_separation（随字号缩放）
			const float chrome = 140f;         // 返回按钮 + 面板内边距 + 余量
			int maxByWidth = Mathf.Max(3, (int)((vp.X - 32f) / colPitch));
			int cols = Mathf.Min(6, maxByWidth);
			while (cols < maxByWidth
				&& (items.Count + cols - 1) / cols * rowPitch + chrome > vp.Y)
			{
				cols++;
			}
			FillGrid(grid, items, cols);
			Resize(t, items.Count, cols);
			Log("「显示所有标签」：" + items.Count + " 个标签 / " + cols + " 列 / 视口 " + vp);
		}
		catch { }
	}

	/// <summary>把条目填进网格。</summary>
	private void FillGrid(GridContainer grid, List<(string Text, Action Act)> items, int cols)
	{
		try
		{
			grid.Columns = cols;
			foreach ((string text, Action act) in items)
			{
				Button b = MakeButton(text);
				if (act != null)
				{
					Action a = act;
					b.Pressed += () => a();
				}
				grid.AddChild(b);
			}
		}
		catch { }
	}

	/// <summary>
	/// 按条目数估算面板**高度**（只是兜底下限；面板是容器，会按内容自适应）。
	/// **宽度交给内容** —— 每个按钮都有随字号缩放的最小宽，GridContainer 会算对，
	/// 而返回按钮在网格外，不会把列宽撑歪。
	/// </summary>
	private void Resize(Target t, int count, int cols)
	{
		try
		{
			int rowH = BtnMinH() + 6;      // 随字号缩放
			int rows = (count + cols - 1) / cols;
			if (rows < 1)
			{
				rows = 1;
			}
			bool hasBack = false;
			try
			{
				hasBack = t.Dropdown.GetNodeOrNull("WCC_Box/WCC_Back") != null;
			}
			catch { }
			// 有返回按钮时多留一行（它在 VBox 里独占一行）
			float h = rows * rowH + (rows - 1) * 6 + 16 + (hasBack ? rowH : 0);
			t.Dropdown.CustomMinimumSize = new Vector2(0f, h);
			PlaceFor(t);
		}
		catch { }
	}

	/// <summary>展开 / 收起下拉（每次展开都回到第一级）。</summary>
	private void ToggleFor(Target t)
	{
		try
		{
			if (t.Dropdown == null || !GodotObject.IsInstanceValid(t.Dropdown))
			{
				return;
			}
			bool willShow = !t.Dropdown.Visible;
			if (willShow)
			{
				// ★ v2.9.1：首次展开的页面由「默认显示哪一页」配置决定——
				//   不分类 = ShowAllTags（一页铺开全部标签）；分类 = RebuildDropdown（5 大类分组）。
				//   之前在进关时预设（ApplyDefaultCategory），但 InjectIntoTarget 同帧稍后
				//   会再跑一次 RebuildDropdown 覆盖掉 ⇒ 改到点击时刻设置，时序上不可被覆盖。
				if (string.IsNullOrEmpty(_defaultCat))
				{
					ShowAllTags(t);          // 不分类 = 一页全部标签
				}
				else if (_defaultCat == "章节")
				{
					ShowLevelSourcePage(t);  // 按获取章节 = 先显示章节列表
				}
				else
				{
					RebuildDropdown(t);      // 分类 = 5 大类分组
				}
				PlaceFor(t);
			}
			t.Dropdown.Visible = willShow;
		}
		catch { }
	}

	/// <summary>
	/// 面板刚重建时的**延迟尺寸补偿**：容器的最小尺寸是 `queue_sort()` 延迟算的
	/// ⇒ 同帧 `PlaceFor` 可能拿到偏小的尺寸，把面板贴得偏低、甚至底部越界。
	/// 这里让接下来几帧各重贴一次，等尺寸稳定后位置就准了。
	/// </summary>
	private void FlushRePlace(Target t)
	{
		try
		{
			if (t == null || t.PlaceFrames <= 0)
			{
				return;
			}
			if (t.Dropdown == null || !GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.PlaceFrames = 0;
				return;
			}
			t.PlaceFrames--;
			PlaceFor(t);
		}
		catch
		{
			try { t.PlaceFrames = 0; } catch { }
		}
	}

	/// <summary>用按钮的实际屏幕矩形定位（右边放不下就翻到左侧，并夹到视口内）。</summary>
	private void PlaceFor(Target t)
	{
		try
		{
			if (t.Button == null || !GodotObject.IsInstanceValid(t.Button)
				|| t.Dropdown == null || !GodotObject.IsInstanceValid(t.Dropdown))
			{
				return;
			}
			Control vis = t.Button.GetNodeOrNull<Control>("SpriteBrightButton");
			Rect2 r = (vis != null && GodotObject.IsInstanceValid(vis))
				? new Rect2(vis.GetGlobalPosition(), vis.Size * vis.Scale)
				: new Rect2(t.Button.GetGlobalPosition(), new Vector2(60f, 60f));

			Vector2 size = t.Dropdown.GetCombinedMinimumSize();
			if (size.X < 40f || size.Y < 20f)
			{
				size = t.Dropdown.CustomMinimumSize;
			}
			Vector2 vp = new Vector2(1920f, 1080f);
			try
			{
				Vector2 vs = _tree.Root.GetVisibleRect().Size;
				if (vs.X > 1f && vs.Y > 1f)
				{
					vp = vs;
				}
			}
			catch { }

			float x = r.Position.X + r.Size.X + 8f;
			if (x + size.X > vp.X - 8f)
			{
				x = r.Position.X - size.X - 8f;      // 右边放不下 ⇒ 翻到左侧
			}
			x = Mathf.Clamp(x, 8f, Mathf.Max(8f, vp.X - size.X - 8f));
			float y = Mathf.Clamp(r.Position.Y - 60f, 8f, Mathf.Max(8f, vp.Y - size.Y - 8f));
			t.Dropdown.GlobalPosition = new Vector2(x, y);
		}
		catch { }
	}

	/// <summary>点某个标签 ⇒ 收起下拉 + 走该卡池自己的 `CategoryChoose`。</summary>
	private void OnCategoryClicked(Target t, string cat)
	{
		try
		{
			// ★ v2.13：图鉴没有 CategoryChoose ⇒ 走本地筛选（只作用于当前图鉴页）
			if (ReferenceEquals(t, _almanac))
			{
				FilterAlmanacPage(t, BakeKeysWithTag(cat), cat);
				return;
			}
			if (t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.Dropdown.Visible = false;
			}
			// ★ v2.12.3（用户实测：章节能用、标签点了是空池）：
			//   与 `OnChapterClicked` 保持一致 —— **点击时**按烘焙表把该标签的植物列表写进
			//   `pb.category`。原因：注入时那份只保留「当前卡池里真实存在的白卡」，
			//   若某个标签在本关卡池里恰好为空（或烘焙表与设备侧卡池不同步），
			//   注入阶段会因 `Count > 0` 判定而**跳过**该键 ⇒ 点击时 CategoryChoose 查不到键 ⇒ 卡池被清空。
			//   章节正是因为"点击时现写"才一直能用 ✓
			try
			{
				TowerDefensePacketBankData pb = TowerDefenseManager.Instance?.GetPacketBankFeature()?.packetBankData;
				if (pb != null && GodotObject.IsInstanceValid(pb))
				{
					EnsureBaked();
					var arr = new Godot.Collections.Array();
					foreach (KeyValuePair<string, string[]> kv in _baked)
					{
						foreach (string tg in kv.Value)
						{
							if (string.Equals(tg, cat, StringComparison.Ordinal)) { arr.Add(kv.Key); break; }
						}
					}
					if (arr.Count > 0)
					{
						pb.category[KeyPrefix + cat] = arr;
						Log("已按烘焙表写入标签「" + cat + "」的 " + arr.Count + " 株植物。");
					}
				}
			}
			catch { }
			if (t.Choose != null)
			{
				t.Choose(KeyPrefix + cat);
			}
			Log("已切换到白卡标签：「" + cat + "」");
		}
		catch (Exception ex)
		{
			if (_diag < 106)
			{
				_diag = 106;
				Log("切换标签异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ====================================================== 显示开关（★ v2.13）
	//
	// 两个配置开关（mod.json → 配置页）：
	//   showInBattleSelect = 战斗前选卡界面（含自制关卡编辑器卡牌库）是否显示按钮
	//   showInAlmanac      = 植物图鉴页是否显示按钮
	// 改配置后由 `Settings.Changed` → `OnWccSettingsChanged` → `ApplyShowSwitches()` 立即生效；
	// 建按钮的地方（OnFrame / TickAlmanac）另外用开关做前置判断，避免"关着也建出来再藏"。

	/// <summary>按两个开关显示 / 隐藏三处「白卡分类」按钮。</summary>
	private void ApplyShowSwitches()
	{
		try
		{
			ApplyOneSwitch(_game, _showBattle);
			ApplyOneSwitch(_editor, _showBattle);
			ApplyOneSwitch(_almanac, _showAlmanac);
			Log("显示开关已应用（选卡=" + _showBattle + "，图鉴=" + _showAlmanac + "）。");
		}
		catch { }
	}

	/// <summary>显示 / 隐藏单个目标上的按钮；关掉时顺手把下拉面板收起。</summary>
	private static void ApplyOneSwitch(Target t, bool show)
	{
		try
		{
			if (t == null)
			{
				return;
			}
			if (t.Button != null && GodotObject.IsInstanceValid(t.Button))
			{
				t.Button.Visible = show;
			}
			if (!show && t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.Dropdown.Visible = false;
			}
		}
		catch { }
	}

	// ======================================================== 植物图鉴（★ v2.13）
	//
	// 图鉴节点：`Almanac`（DialogBoxBase，无命名空间，public）由 `DialogManager.DialogCreate("Almanac")`
	// 挂在 `%DialogLayer` 下；**每次打开都是新实例，关闭即 QueueFree**。
	// 植物页结构：PlantLayer(CanvasLayer, layer=51) / Plant(Control 全屏) /
	//   { TextureRect, TitleLabel, PacketScroll, IndexButton(左下), PlantPreButton, PlantNextButton,
	//     ShowButton(右下), PlantInformationPanel }  —— 左上角是空的，按钮就放那儿。
	//
	// 图鉴没有 `CategoryChoose`（那是 TowerDefenseBattleFeaturePacketBank 的方法），
	// 所以不能沿用"写 pb.category + CategoryChoose"那套。但 `Almanac.InitPlant()`
	// 是**公开**的、且它的数据源就是 `plantPacketBank.category[plantCategoryId]`
	// ⇒ 只要把当前页那个键的数组临时换成筛选结果，再调一次 `InitPlant()`，
	//    图鉴就按筛选结果重建卡片列表；`plantCategoryId` 一动不动，
	//    上一页 / 下一页 / 页数 / 收藏排序等语义全部保持原样。

	/// <summary>图鉴实例查找（缓存优先；没有缓存时限频全树扫描，避免每帧 FindChild）。</summary>
	private Almanac ResolveAlmanac()
	{
		try
		{
			if (_almanacNode != null && GodotObject.IsInstanceValid(_almanacNode) && _almanacNode.IsInsideTree())
			{
				return _almanacNode;
			}
			if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root))
			{
				return null;
			}
			if ((_frame % 5) != 0)
			{
				return null;      // 约 80ms 探一次，够快也不浪费
			}
			Node found = _tree.Root.FindChild("Almanac", true, false);
			if (found is Almanac am && GodotObject.IsInstanceValid(am))
			{
				return am;
			}
		}
		catch { }
		return null;
	}

	/// <summary>清理图鉴侧的缓存引用（对话框关闭 / 换成新实例时调）。</summary>
	private void ResetAlmanacState()
	{
		try { RestoreAlmanacFilter(); } catch { }
		_almanacNode = null;
		_almanac.Button = null;
		_almanac.Dropdown = null;
		_almanac.Parent = null;
		_almanac.DataId = 0;
		_almanac.GroupTags = null;
		_almanac.OtherCount = 0;
		_almanac.Choose = null;
		_almanac.ChooseHandler = null;
		_almanac.AlmanacNode = null;
		_almanac.AlmanacFilter = null;
		_almanacLastCatId = -1;
	}

	/// <summary>每帧驱动图鉴侧：找实例 → 左上角挂按钮 → 建下拉 → 翻页时撤销筛选。</summary>
	private void TickAlmanac()
	{
		try
		{
			Almanac a = ResolveAlmanac();
			if (a == null)
			{
				if (_almanacNode != null || _almanac.Button != null || _almanac.Dropdown != null)
				{
					ResetAlmanacState();          // 图鉴关了：面板随对话框一起没了，清引用即可
				}
				return;
			}
			if (!ReferenceEquals(a, _almanacNode))
			{
				ResetAlmanacState();
				_almanacNode = a;
				_almanac.AlmanacNode = a;
				_almanacLastCatId = a.plantCategoryId;
				Log("已接上图鉴实例（plantCategoryId=" + a.plantCategoryId + "）。");
			}

			if (!_showAlmanac)
			{
				ApplyOneSwitch(_almanac, false);
				return;                        // 开关关着：不建按钮
			}

			Control plantRoot = a.GetNodeOrNull<Control>("PlantLayer/Plant");
			if (plantRoot == null || !GodotObject.IsInstanceValid(plantRoot))
			{
				return;
			}

			PacketCategoryButton btn = EnsureButton(_almanac, plantRoot, "白卡分类", manualPos: true);
			if (btn == null)
			{
				return;
			}
			// 选卡界面那份用的是 CardSort 下的坐标（133,362）；图鉴这边覆盖到**左上角**。
			btn.Position = new Vector2(AlmanacButtonLeft, AlmanacButtonTop);
			btn.Visible = true;
			SetLightMaskRecursive(btn, 0);   // 图鉴里 Plant 整棵树的 light_mask 都是 0，保持一致

			// 图鉴不走 InjectIntoTarget（那会写战斗卡池），标签列表直接从它自己的白卡副本建一次
			if (_almanac.GroupTags == null || _almanac.GroupTags.Count == 0)
			{
				BuildGroupTagsForAlmanac(a);
			}

			EnsureDropdown(_almanac, plantRoot);
			FlushRePlace(_almanac);

			// 上一页 / 下一页 ⇒ 先撤销上一次的临时替换。
			// 只还原**旧键**，当前页用的是它自己的原始数组，所以不需要再跑一次 InitPlant。
			if (_almanacLastCatId != a.plantCategoryId)
			{
				_almanacLastCatId = a.plantCategoryId;
				RestoreAlmanacFilter();
			}
		}
		catch { }
	}

	/// <summary>用图鉴自己那份卡池里的 "White" 数组建标签索引（不碰战斗卡池）。</summary>
	private void BuildGroupTagsForAlmanac(Almanac a)
	{
		try
		{
			TowerDefensePacketBankData pb = a?.plantPacketBank;
			if (pb == null || !GodotObject.IsInstanceValid(pb))
			{
				return;
			}
			var set = new HashSet<string>();
			if (pb.category.ContainsKey("White"))
			{
				Godot.Collections.Array white = pb.category["White"].AsGodotArray();
				if (white != null)
				{
					foreach (Variant v in white)
					{
						set.Add(v.AsString());
					}
				}
			}
			if (set.Count == 0)
			{
				return;
			}
			EnsureBaked();
			BuildGroupTags(set, _almanac);
			Log("图鉴：白卡 " + set.Count + " 株，已建 " + (_almanac.GroupTags?.Count ?? 0) + " 个大类。");
		}
		catch { }
	}

	/// <summary>把一棵子树里所有 CanvasItem 的 light_mask 设成同一个值（图鉴里整棵树都是 0）。</summary>
	private static void SetLightMaskRecursive(Node node, int mask)
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node))
			{
				return;
			}
			if (node is CanvasItem ci)
			{
				ci.LightMask = mask;
			}
			foreach (Node child in node.GetChildren())
			{
				SetLightMaskRecursive(child, mask);
			}
		}
		catch { }
	}

	/// <summary>烘焙表里带某个标签的全部植物键集合。</summary>
	private static HashSet<string> BakeKeysWithTag(string cat)
	{
		var set = new HashSet<string>();
		try
		{
			EnsureBaked();
			foreach (KeyValuePair<string, string[]> kv in _baked)
			{
				foreach (string tg in kv.Value)
				{
					if (string.Equals(tg, cat, StringComparison.Ordinal))
					{
						set.Add(kv.Key);
						break;
					}
				}
			}
		}
		catch { }
		return set;
	}

	/// <summary>
	/// 图鉴筛选核心：把**当前页**那个 category 键的数组临时换成「原数组 ∩ wanted」，
	/// 再调 `InitPlant()` 让图鉴按新数组重建卡片列表。
	/// `plantCategoryId` 不动 ⇒ 翻页 / 页数 / 收藏排序语义完全不变。
	/// </summary>
	private void FilterAlmanacPage(Target t, ICollection<string> wanted, string label)
	{
		try
		{
			if (t != null && t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.Dropdown.Visible = false;     // 选完就收面板
			}
			Almanac a = _almanacNode;
			if (a == null || !GodotObject.IsInstanceValid(a))
			{
				return;
			}
			TowerDefensePacketBankData pb = a.plantPacketBank;
			if (pb == null || !GodotObject.IsInstanceValid(pb))
			{
				return;
			}

			// 每次都先还原上一次的临时替换 ⇒ 基于「原始页」求交集（连点不同标签不会层层收窄）
			RestoreAlmanacFilter();

			var keys = new Godot.Collections.Array();
			foreach (Variant k in pb.category.Keys)
			{
				keys.Add(k);
			}
			int idx = a.plantCategoryId;
			if (idx < 0 || idx >= keys.Count)
			{
				return;
			}
			string curKey = keys[idx].AsString();
			if (!pb.category.ContainsKey(curKey))
			{
				return;
			}
			Godot.Collections.Array original = pb.category[curKey].AsGodotArray();
			if (original == null)
			{
				return;
			}

			var filtered = new Godot.Collections.Array();
			if (wanted != null)
			{
				foreach (Variant v in original)
				{
					string k = v.AsString();
					if (wanted.Contains(k))
					{
						filtered.Add(k);
					}
				}
			}
			if (filtered.Count == 0)
			{
				// ★ 本页没有该标签的植物 ⇒ 自动跳到「含有最多该标签植物」的那一页，
				//   而不是什么都不做（那样用户会以为点了没反应 / 卡丢了）。
				int bestIdx = -1;
				int bestCount = 0;
				Godot.Collections.Array bestArr = null;
				for (int i = 0; i < keys.Count; i++)
				{
					string k2 = keys[i].AsString();
					if (!pb.category.ContainsKey(k2)) { continue; }
					Godot.Collections.Array arr2 = pb.category[k2].AsGodotArray();
					if (arr2 == null) { continue; }
					int hit = 0;
					foreach (Variant v2 in arr2)
					{
						if (wanted != null && wanted.Contains(v2.AsString())) { hit++; }
					}
					if (hit > bestCount)
					{
						bestCount = hit;
						bestIdx = i;
						bestArr = arr2;
						curKey = k2;
						original = arr2;
					}
				}
				if (bestIdx < 0 || bestArr == null || bestCount == 0)
				{
					Log("图鉴：标签「" + label + "」在植物图鉴里找不到对应植物。");
					return;
				}
				// 切到那一页（plantCategoryId 变了，TickAlmanac 会看到并保持不清筛选，
				// 因为我们同步把 _almanacLastCatId 设成了新下标）
				a.plantCategoryId = idx = bestIdx;
				filtered = new Godot.Collections.Array();
				foreach (Variant v3 in bestArr)
				{
					string k3 = v3.AsString();
					if (wanted != null && wanted.Contains(k3)) { filtered.Add(k3); }
				}
				Log("图鉴：本页没有「" + label + "」，已自动跳到有 " + filtered.Count + " 株的那一页。");
			}

			_almanacFilterKey = curKey;
			_almanacFilterBackup = original.Duplicate();
			_almanacFilterActive = true;
			_almanacLastCatId = idx;
			pb.category[curKey] = filtered;
			a.InitPlant();
			Log("图鉴：已筛选「" + label + "」，本页 " + filtered.Count + "/" + original.Count + " 株。");
		}
		catch (Exception ex)
		{
			if (_diag < 108)
			{
				_diag = 108;
				Log("图鉴筛选异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>把被临时替换的 category 键写回原数组。</summary>
	private void RestoreAlmanacFilter()
	{
		if (!_almanacFilterActive)
		{
			return;
		}
		_almanacFilterActive = false;
		try
		{
			Almanac a = _almanacNode;
			if (a != null && GodotObject.IsInstanceValid(a)
				&& _almanacFilterBackup != null
				&& _almanacFilterKey.VariantType == Variant.Type.String)
			{
				TowerDefensePacketBankData pb = a.plantPacketBank;
				if (pb != null && GodotObject.IsInstanceValid(pb))
				{
					pb.category[_almanacFilterKey] = _almanacFilterBackup;
				}
			}
		}
		catch { }
		_almanacFilterBackup = null;
		_almanacFilterKey = default;
	}

	/// <summary>图鉴侧的「取消筛选」：还原本页原数组 + 让图鉴重建。</summary>
	private void ClearAlmanacFilter(Target t)
	{
		try
		{
			if (t != null && t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.Dropdown.Visible = false;
			}
			RestoreAlmanacFilter();
			Almanac a = _almanacNode;
			if (a != null && GodotObject.IsInstanceValid(a))
			{
				a.InitPlant();
			}
			Log("图鉴：已取消筛选。");
		}
		catch { }
	}

	private void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}
