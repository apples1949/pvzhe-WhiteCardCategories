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
	private const string P = "[WhiteCardCat] ";

	/// <summary>注入到 packetBankData 的标签键前缀（避免与游戏自带键撞车）。</summary>
	private const string KeyPrefix = "WCC_";

	/// <summary>诊断日志（默认关；排查时置 true 重新构建）。</summary>
	private static readonly bool EnableLog = false;

	/// <summary>字号选择项（用户要求：16/18/20/22/24/26）。</summary>
	private static readonly int[] FontSizeChoices = { 16, 18, 20, 22, 24, 26 };

	/// <summary>配置持久化文件（Godot user:// = %APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\）。</summary>
	private const string ConfigPath = "user://WhiteCardCategories.cfg";

	/// <summary>当前按钮字号（持久化；默认 18）。</summary>
	private int _fontSize = 18;

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

	public void Initialize(XWModRuntimeContext context)
	{
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
				if (vb != null && EnsureButton(_editor, vb, "白卡分类", manualPos: false) != null)
				{
					EnsureDropdown(_editor, eb);
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
			if (t.DataId == pb.GetInstanceId() && t.GroupTags != null && t.GroupTags.Count > 0)
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

	// ================================================================ 建 UI

	/// <summary>
	/// 一套"卡池目标"的 UI 状态 —— 游戏内选卡界面与**自制关卡的编辑卡牌库**
	/// 各持一份，代码完全共用。
	/// </summary>
	private sealed class Target
	{
		public ulong DataId;                            // 已注入的那份卡池数据（实例 ID）
		public Dictionary<string, List<string>> GroupTags;  // 大类 → 非空标签（空大类不放）
		public int OtherCount;                           // 「其他」里的卡数
		public PacketCategoryButton Button;              // 「白卡分类」按钮
		public Control Dropdown;                         // 自绘二级下拉面板
		public Control Parent;                           // 下拉面板挂载的父节点（重建面板时要用）
		public int PlaceFrames;                          // 还需重贴几帧（容器最小尺寸是延迟算的）
		public Action<string> Choose;                    // 点标签后的切换回调
	}

	private readonly Target _game = new Target();     // 游戏内选卡界面
	private readonly Target _editor = new Target();   // 自制关卡的编辑卡牌库

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
			btn.OnChoose += _ => ToggleFor(t);
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
			FillGrid(grid, items, cols: 2);
			Resize(t, items.Count, 2);
			AddFontRow(t);      // ★ 第一级末尾追加一行「字号」选择
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
				RebuildDropdown(t);      // 回到第一级
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
			if (t.Dropdown != null && GodotObject.IsInstanceValid(t.Dropdown))
			{
				t.Dropdown.Visible = false;
			}
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

	private void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}
