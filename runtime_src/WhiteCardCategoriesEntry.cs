using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「白卡分类」Mod —— 在选卡界面右侧分类栏「僵尸卡」下方新增一个「白卡分类」按钮，
/// 点开后弹出二级下拉列出植物的功能类别（阳光 / 豌豆射手 / 坚果防御 / 水草水生 / 僵尸系 …），
/// 点某类别即把**待选区**切换成该类别的植物。**只对白卡生效**（用户指定），**一张卡可属多个类别**。
///
/// ── 落点（读源码核实，2026-09-30）────────────────────────────────
/// `TowerDefenseInGamePacketBank.tscn`：
///   Translate/CardSort                     ← 右侧分类面板（TextureRect，94 宽）
///     ├ CardMod(y= 62) CardItem(y=137) CardGraveStone(y=212) CardZombie(y=287)
///     └ VBoxContainer/  ← 左列 白卡/金卡/钻卡/彩卡/星卡/原卡
///   每格竖直间距 **75px** ⇒ 「僵尸卡」下面是 **y=362**（用户红框位置）。
///   每个格子都是 `PacketCategoryButton` 场景实例：
///       SpriteBrightButton(图) + LabelNode/Label(文字)   ← **图像+文字**合成，用户要的就是这个
///   点击链：`PacketCategoryButton._Ready()` → `SpriteBrightButton.OnPressed += ButtonPressed`
///          → `OnChoose?.Invoke(category)`。
///   **游戏只给自己那 4 个按钮订阅 `OnChoose`** ⇒ 我们的克隆按钮由本 Mod 自行订阅。
///
/// ── 切换待选区走原生路径 ─────────────────────────────────────────
/// `TowerDefenseBattleFeaturePacketBank.CategoryChoose(类别键)` —— 游戏自己的分类切换函数，
/// 它读 `packetBankData.category[类别键]` 重建卡池 ⇒ 我们只需把自定义类别**注入**
/// `packetBankData.category`（键加前缀避免与游戏键撞车），再调用它。
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
	private const string P = "[WhiteCardCat] ";

	/// <summary>注入到 packetBankData 的类别键前缀（避免与游戏自带键撞车）。</summary>
	private const string KeyPrefix = "WCC_";

	/// <summary>诊断日志（默认开 —— 第一版要看注入/点击是否成功；稳定后改 false）。</summary>
	private static readonly bool EnableLog = true;

	/// <summary>「白卡分类」按钮在 CardSort 下的位置（与 CardZombie 同列，向下 75px）。</summary>
	private const float ButtonOffsetLeft = 133f;
	private const float ButtonOffsetTop = 362f;

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	/// <summary>已建好 UI 的 packetBank 实例 ID（换关卡会重建）。</summary>
	private ulong _uiBankId;

	/// <summary>已注入类别的 packetBankData 实例 ID。</summary>
	private ulong _injectedDataId;

	private PacketCategoryButton _ourButton;
	private Control _dropdown;
	private int _frame;

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			Log("初始化完成。将在选卡界面「僵尸卡」下方新增「白卡分类」（只覆盖白卡，多标签）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
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
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_frame++;

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
			}
			if (EnsureButton(_game, sort, "白卡分类", manualPos: true) != null)
			{
				EnsureDropdown(_game, sort);
			}

			// ── ② 自制关卡（关卡编辑器）的编辑卡牌库 ────────────────
			//   ★ v1.2.1：那一套卡池是**另一个类** `LevelEditorPacketBank`，
			//     数据来自全局 `PACKETBANKS["Total"]`，同一个"白卡分类"下拉在这里也适用。
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
				if (vb != null && EnsureButton(_editor, vb, "白卡分类", manualPos: false) != null)
				{
					EnsureDropdown(_editor, eb);
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

	// ================================================================ 类别注入

	/// <summary>
	/// 把本 Mod 的类别写进当前关卡的 `packetBankData.category`。
	/// 每个类别只保留**当前卡池里真实存在**的白卡（关卡卡池可能是子集）。
	/// `packetBankData` 每次进关卡都会被 `SetPacketBankData()` 重建 ⇒ 按实例 ID 判断是否需要重做。
	/// </summary>
	private void InjectIntoTarget(TowerDefensePacketBankData pb, Target t)
	{
		try
		{
			if (pb == null || !GodotObject.IsInstanceValid(pb))
			{
				return;
			}
			if (t.DataId == pb.GetInstanceId() && t.Order != null && t.Order.Count > 0)
			{
				return;      // 这份卡池数据已经注入过
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

			// ── 逐卡归类别：烘焙表 ∪ 运行时判定；两者都没命中 ⇒ 「其他」兜底 ──
			EnsureTableIndex();
			var buckets = new Dictionary<string, Godot.Collections.Array>();
			var ordered = new List<string>();
			foreach (string cat in WhiteCardCategoriesTable.Order)
			{
				ordered.Add(cat);
				buckets[cat] = new Godot.Collections.Array();
			}
			buckets["其他"] = new Godot.Collections.Array();

			int total = 0;
			foreach (string k in whiteSet)
			{
				var set = new HashSet<string>();
				if (_tableIndex.TryGetValue(k, out List<string> baked))
				{
					foreach (string c in baked)
					{
						set.Add(c);
					}
				}
				ClassifyRuntime(k, set);
				if (set.Count == 0)
				{
					set.Add("其他");
				}
				foreach (string c in set)
				{
					if (!buckets.ContainsKey(c))
					{
						buckets[c] = new Godot.Collections.Array();
						ordered.Add(c);
					}
					buckets[c].Add(k);
					total++;
				}
			}
			foreach (KeyValuePair<string, Godot.Collections.Array> kv in buckets)
			{
				if (kv.Value.Count > 0)
				{
					pb.category[KeyPrefix + kv.Key] = kv.Value;
				}
			}
			if (buckets["其他"].Count > 0)
			{
				ordered.Add("其他");     // 有落单的才显示「其他」（用户要求）
			}
			t.Order = ordered;
			t.DataId = pb.GetInstanceId();
			Log("已注入白卡子类别 " + ordered.Count + " 个（映射 " + total + " 条；兜底「其他」"
				+ buckets["其他"].Count + " 张）。");
		}
		catch (Exception ex)
		{
			if (_diag < 101)
			{
				_diag = 101;
				Log("类别注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 运行时兜底判定

	/// <summary>烘焙表的反查索引：卡 key → 它所属的类别列表。</summary>
	private Dictionary<string, List<string>> _tableIndex;

	/// <summary>下拉栏要显示的类别顺序（含运行时补上的「其他」）。</summary>
	private List<string> _orderedCats;

	private void EnsureTableIndex()
	{
		if (_tableIndex != null)
		{
			return;
		}
		_tableIndex = new Dictionary<string, List<string>>();
		foreach (KeyValuePair<string, string[]> kv in WhiteCardCategoriesTable.Map)
		{
			foreach (string k in kv.Value)
			{
				if (!_tableIndex.TryGetValue(k, out List<string> list))
				{
					list = new List<string>();
					_tableIndex[k] = list;
				}
				list.Add(kv.Key);
			}
		}
	}

	// 与生成脚本同源的关键词表（★ 一律小写比较，避免 "Pult" 漏掉 "pult" 这种大小写坑）
	private static readonly (string Cat, string[] Kw)[] RuntimeRules = new (string, string[])[]
	{
		("阳光", new[] { "sun" }),
		("豌豆射手", new[] { "pea", "peater", "gatling", "shooter", "repeater" }),
		("投掷", new[] { "pult", "cob", "cannon" }),
		("坚果防御", new[] { "nut", "pumpkin" }),
		("地刺", new[] { "spike", "caltrop" }),
		("水草水生", new[] { "lily", "sea", "kelp", "tangle", "cattail" }),
		("爆炸一次性", new[] { "bomb", "mine", "cherry", "squash", "potato", "jalapeno", "jala" }),
		("大嘴花", new[] { "chomper" }),
		("磁力", new[] { "magnet" }),
		("冰冻", new[] { "ice", "snow", "frozen" }),
		("火焰", new[] { "fire", "torch", "flame" }),
		("蘑菇", new[] { "shroom" }),
		("猫尾", new[] { "catflower", "catpumpkin", "catpot", "cattail" }),
		("大蒜", new[] { "garlic" }),
		("墓碑清理", new[] { "gravebuster" }),
		("催眠", new[] { "hypno" }),
		("灯光", new[] { "plantern", "lantern", "lamp", "light" }),
		("咖啡", new[] { "coffee" }),
		("吹风清障", new[] { "blover" }),
		("魔法", new[] { "magic" }),
		("模仿者", new[] { "imitater" }),
		("僵尸系", new[] { "zombie" }),
		("辅助道具", new[] { "bean", "sale", "upgrade", "transfer", "present", "root", "gold" }),
	};

	// 配置标志位（与源码 TowerDefenseEnum 一致）
	private const int PhyNut = 1;         // CHARACTER_PHYSIQUE_TYPE.NUT
	private const int PhyPot = 2;         // POT
	private const int PhySpike = 0x10;    // SPIKE
	private const int PhyCat = 0x100;     // CAT
	private const int PhyMagic = 0x1000;  // MAGIC
	private const int GtWater = 3;        // PLANTGRIDTYPE.WATER
	private const int GtLilypad = 5;      // PLANTGRIDTYPE.LILYPAD
	private const int GtGravestone = 8;   // PLANTGRIDTYPE.GRAVESTONE
	private const int ElIce = 1;          // ELEMENT_SYSTEM.ICE
	private const int ElFire = 2;         // ELEMENT_SYSTEM.FIRE

	/// <summary>
	/// 运行时兜底判定：**配置标志位 + 名称关键词**。烘焙表没覆盖到的卡（游戏更新新增的）靠它归类。
	/// 还判不出来的，调用方会把它丢进「其他」。
	/// </summary>
	private void ClassifyRuntime(string key, HashSet<string> outCats)
	{
		try
		{
			string lk = key.ToLowerInvariant();
			foreach ((string cat, string[] kws) in RuntimeRules)
			{
				foreach (string w in kws)
				{
					if (lk.Contains(w))
					{
						outCats.Add(cat);
						break;
					}
				}
			}
			if (lk.EndsWith("z"))
			{
				outCats.Add("僵尸系");
			}

			// ── 配置标志位（新卡同样有效）──
			TowerDefensePacketConfig cfg = TowerDefenseManager.GetPacketConfig(key);
			if (cfg != null && GodotObject.IsInstanceValid(cfg)
				&& cfg.characterConfig is TowerDefensePlantConfig pc)
			{
				// ★ 坚果：高血量也算肉盾（实测 hp>=3000 只多收 2 张且都合理）
			if (pc.hitpoints >= 3000.0)
			{
				outCats.Add("坚果防御");
			}
			int phy = pc.physiqueTypeFlags;
				if ((phy & PhyNut) != 0) { outCats.Add("坚果防御"); }
				if ((phy & PhyPot) != 0) { outCats.Add("花盆"); }
				if ((phy & PhySpike) != 0) { outCats.Add("地刺"); }
				if ((phy & PhyCat) != 0) { outCats.Add("猫尾"); }
				if ((phy & PhyMagic) != 0) { outCats.Add("魔法"); }
				int el = pc.elementFlags;
				if ((el & ElIce) != 0) { outCats.Add("冰冻"); }
				if ((el & ElFire) != 0) { outCats.Add("火焰"); }
				// ★ v1.2.0：「水草水生」只认真水生（关键词，运行时拿不到组件集）；
				//   "只是能种在水里/荷叶上" 另归「可种水中」——与生成脚本的 elif 口径一致。
				bool waterGrid = false;
				foreach (TowerDefenseEnum.PLANTGRIDTYPE g in pc.plantGridType)
				{
					int gv = (int)g;
					if (gv == GtWater || gv == GtLilypad) { waterGrid = true; }
					if (gv == GtGravestone) { outCats.Add("墓碑清理"); }
				}
				if (waterGrid && !outCats.Contains("水草水生"))
				{
					outCats.Add("可种水中");
				}
			}
		}
		catch { }
	}

	// ================================================================ 建 UI（两套卡池通用）

	/// <summary>
	/// 一套"卡池目标"的 UI 状态 —— 游戏内选卡界面与**自制关卡的编辑卡牌库**
	/// 各持一份，代码完全共用。
	/// </summary>
	private sealed class Target
	{
		public ulong DataId;                 // 已注入类别的那份 packetBankData
		public List<string> Order;           // 下拉栏类别顺序（含按需的「其他」）
		public PacketCategoryButton Button;  // 「白卡分类」按钮
		public Control Dropdown;             // 自绘下拉面板
		public Action<string> Choose;        // 点类别后的切换回调（各卡池自己的 CategoryChoose）
	}

	private readonly Target _game = new Target();     // 游戏内选卡界面
	private readonly Target _editor = new Target();   // 自制关卡的编辑卡牌库

	/// <summary>
	/// 确保「白卡分类」按钮存在（实例化游戏官方按钮场景，外观与其它分类按钮一致）。
	/// `manualPos = true` ⇒ 按固定偏移摆在「僵尸卡」下方（游戏内选卡界面）；
	/// `manualPos = false` ⇒ 交给容器排版（自制关卡那一列是 `VBoxContainer`）。
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
			btn.Name = manualPos ? "WCC_WhiteCatButton" : "WCC_WhiteCatButtonEditor";
			btn.Visible = true;
			btn.category = KeyPrefix + "WhiteCat";     // 占位（我们不用它，改用自绘下拉）
			Label lb = btn.GetNodeOrNull<Label>("LabelNode/Label");
			if (lb != null)
			{
				lb.Text = label;
			}
			if (manualPos)
			{
				btn.OffsetLeft = ButtonOffsetLeft;
				btn.OffsetTop = ButtonOffsetTop;
				btn.OffsetRight = ButtonOffsetLeft;
				btn.OffsetBottom = ButtonOffsetTop;
			}
			parent.AddChild(btn);
			btn.OnChoose += _ => ToggleFor(t);
			t.Button = btn;
			return btn;
		}
		catch (Exception ex)
		{
			if (_diag < 102)
			{
				_diag = 102;
				Log("建分类按钮异常（本条只报一次）：" + ex.Message);
			}
			return null;
		}
	}

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

	/// <summary>确保该目标的二级下拉存在（宽度随文字自适应、两列、画在最上层）。</summary>
	private void EnsureDropdown(Target t, Control parent)
	{
		try
		{
			if (t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown) && t.Dropdown.IsInsideTree())
			{
				return;
			}
			if (parent == null || !GodotObject.IsInstanceValid(parent))
			{
				return;
			}
			List<string> order = t.Order ?? new List<string>(WhiteCardCategoriesTable.Order);
			const int cols = 2;
			const int rowH = 36;
			const int fontSz = 16;

			PanelContainer panel = new PanelContainer();
			panel.Name = "WCC_Dropdown";
			panel.Visible = false;
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

			GridContainer grid = new GridContainer();
			grid.Columns = cols;
			grid.AddThemeConstantOverride("h_separation", 6);
			grid.AddThemeConstantOverride("v_separation", 4);
			panel.AddChild(grid);

			foreach (string cat in order)
			{
				Button b = new Button();
				b.Text = cat;
				b.CustomMinimumSize = new Vector2(0f, rowH);   // 宽度随文字自适应
				b.Alignment = HorizontalAlignment.Center;
				b.AddThemeFontSizeOverride("font_size", fontSz);

				StyleBoxFlat normal = new StyleBoxFlat();
				normal.BgColor = new Color(0.86f, 0.78f, 0.58f, 1f);
				normal.BorderColor = new Color(0.42f, 0.31f, 0.15f, 1f);
				normal.SetBorderWidthAll(2);
				normal.SetCornerRadiusAll(6);
				b.AddThemeStyleboxOverride("normal", normal);
				StyleBoxFlat hover = new StyleBoxFlat();
				hover.BgColor = new Color(0.97f, 0.90f, 0.68f, 1f);
				hover.BorderColor = new Color(0.42f, 0.31f, 0.15f, 1f);
				hover.SetBorderWidthAll(2);
				hover.SetCornerRadiusAll(6);
				b.AddThemeStyleboxOverride("hover", hover);
				b.AddThemeStyleboxOverride("pressed", hover);
				b.AddThemeColorOverride("font_color", new Color(0.20f, 0.13f, 0.05f));

				string captured = cat;
				b.Pressed += () => OnCategoryClicked(t, captured);
				grid.AddChild(b);
			}

			int rows = (order.Count + cols - 1) / cols;
			panel.CustomMinimumSize = new Vector2(0f, rows * rowH + (rows - 1) * 4 + 16);
			parent.AddChild(panel);
			t.Dropdown = panel;
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

	/// <summary>展开 / 收起下拉，并把位置贴到「白卡分类」按钮右侧。</summary>
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
				PlaceFor(t);
			}
			t.Dropdown.Visible = willShow;
		}
		catch { }
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

	/// <summary>点某个类别 ⇒ 收起下拉 + 走该卡池自己的 `CategoryChoose`。</summary>
	private void OnCategoryClicked(Target t, string cat)
	{
		try
		{
			if (t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.Dropdown.Visible = false;
			}
			if (t.Choose != null)
			{
				t.Choose(KeyPrefix + cat);
			}
			Log("已切换到白卡类别：「" + cat + "」");
		}
		catch (Exception ex)
		{
			if (_diag < 106)
			{
				_diag = 106;
				Log("切换类别异常（本条只报一次）：" + ex.Message);
			}
		}
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
