# 白卡分类（WhiteCardCategories）v2

> **两处落点**：
> 1. **选卡界面**右侧分类栏「僵尸卡」下方 —— 新增「白卡分类」按钮（y=362）；
> 2. **图鉴 → 植物图鉴页左上角** —— 同一款官方多卡牌底按钮（位置 44,34）。
>
> 点开都是**二级菜单**：第一级 **5 大类**（攻击 / 生产 / 职能 / 系列 / 属性），第二级是该大类下的**具体标签**。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `whitecardcategories` |
| 程序集 | `JTYWhiteCardCategories` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `WhiteCardCategoriesEntry` |
| 当前版本 | 2.14.1 |
| 分类 | 工具与前置 |

## 数据来源：《白卡植物标签表.xlsx》

标签体系**完全来自表格**，不是自己编的：

| 表格工作表 | 用到的列 | 用途 |
| --- | --- | --- |
| `标签字典` | **分组** | → **5 大类** |
| `标签字典` | 标签 / 判定依据 / 源码查找路径 | → 运行时自动识别的规则 |
| `白卡植物标签` | 植物键 / **标签** | → 每株植物的标签（**烘焙表**） |

**5 大类**（字典里共 6 组，其中「备注/脚本特化」命中 0、表格说明已并入「其他」，故不设该大类）：

| 大类 | 标签数 | 标签 |
| --- | --- | --- |
| 攻击 | 11 | 一次性、射击、近战、投抛、穿透、猫尾、发射、弹幕、滚动攻击、后向弹道、滚动弹道 |
| 生产 | 3 | 产阳光、产资源、吸金 |
| 职能 | 20 | 坚果、花盆、反击、火炬、地雷、磁力、魅惑、地刺、保护伞、吹飞、冰冻、窝瓜、成长、缠绕、大嘴花、墓吞、治愈、莲叶、击退、胆小 |
| 系列 | 31 | 豌豆、咖啡豆、辣椒、大蒜、寒冰、南瓜壳、杨桃、僵尸、向日葵、灯具、玉米、樱桃、魔法、土豆雷、三叶草、喷菇、树桩、毁灭、全息、仙人掌、忧郁、促销、高坚果、小喷菇、裂荚、西瓜、海兵菇、模仿者、机器、花瓶、不可穿透 |
| 属性 | 6 | 夜行、火、冰、水陆、水生、仅底座 |

覆盖表格全部 **188 株**白卡植物、**71 个标签**。**一张卡可带多个标签**（各标签独立成桶）。

## 菜单结构

```
白卡分类
├─ 攻击（8）          ← 第一级：5 大类（括号=该大类下的**非空**标签数）
│   ├─ ← 返回
│   ├─ 一次性   射击    近战     ← 第二级：该大类的标签（3 列）
│   ├─ 投抛     穿透    猫尾
│   └─ …
├─ 生产（3）
├─ 职能（20）
├─ 系列（28）
├─ 属性（5）
├─ 显示所有标签        ← ★ 一次铺开全部标签（4 列）
│   ├─ ← 显示标签分类    ← ★ 该页首行，回到上面的 5 大类列表
│   └─ 一次性 射击 近战 投抛 穿透 猫尾 产阳光 …（全部非空标签）
└─ 其他（N）           ← 仅当有落单的卡时才出现
```

* **空标签不显示** —— 表格里有 8 个标签（滚动攻击 / 后向弹道 / 滚动弹道 / 机器 / 花瓶 / 不可穿透 / 仅底座 / 脚本特化）在当前白卡池里 0 命中，菜单里不会出现空分类。所以「攻击」显示 8 而不是 11。
* **返回按钮放在网格外**（面板顶部的 VBox 里，独占一行）。若放进 `GridContainer`，GridContainer 会按最宽子项把**所有列**一起撑宽 ——「← 显示标签分类」比标签名长得多，会把 4 列都拉宽、标签之间留下大片空白。
* 每次展开菜单都**回到第一级**；卡池变化（换关卡）时菜单内容自动刷新。

## 植物图鉴页（v2.13 新增）

图鉴植物页的**左上角**（`PlantLayer/Plant` 下，坐标 44,34）挂同一个
`PacketCategoryButton.tscn`，按钮文字「白卡分类」，点开是同一套标签面板。

**筛选机制与选卡界面完全不同**，因为图鉴没有 `CategoryChoose`：

| | 选卡界面 | 图鉴 |
| --- | --- | --- |
| 数据源 | `TowerDefenseBattleFeaturePacketBank.packetBankData` | `Almanac.plantPacketBank`（`XWModContentCatalog.WithPlants` 的**深拷贝副本**） |
| 切换方式 | 写 `category["WCC_<标签>"]` 后调 `packetBankData.CategoryChoose(键)` | 把**当前页那个 category 键的数组**临时换成「原数组 ∩ 标签集合」，再调 `Almanac.InitPlant()` |
| `plantCategoryId` | —— | **完全不动** ⇒ 上一页/下一页/页数/收藏排序语义不变 |
| 取消 | 再点别的分类 | 面板里的「✕ 取消筛选」，或直接翻页（自动还原） |

* `InitPlant()` 是 `public` 的，只读 `plantPacketBank.category[plantCategoryId]` ⇒
  换掉那个键的内容 = 官方支持的筛选入口，**不用反射、不用 Harmony**。
* 筛选只在**当前页**生效；本页没有该标签的植物时，会自动跳到「含该标签最多」的那一页。
* 连点不同标签不会层层收窄 —— 每次筛选前先还原上一次的临时替换，永远基于**原始页**求交集。
* 翻页（`plantCategoryId` 变化）⇒ 自动撤销临时替换，回到正常浏览。

## 两个显示开关（v2.13 新增）

选项 → Mod 管理 → 白卡分类 → 配置 → **显示** 分组：

| 设置键 | 默认 | 作用 |
| --- | --- | --- |
| `showInBattleSelect` | 开 | 战斗前选卡界面（含自制关卡编辑卡牌库）是否显示「白卡分类」按钮 |
| `showInAlmanac` | 开 | 植物图鉴页是否显示「白卡分类」按钮 |

`applyMode: immediate`，改完立刻生效 —— 关闭时是**隐藏已有按钮**（不是只在创建时跳过），
所以战斗中/图鉴打开着时改也会立即消失。

## 按获取章节（v2.14 新增）

第一级菜单底部的 **「按获取章节」** 入口，按**图鉴里写的获取方式**把白卡分成章节组，
而不是按 `.tres` 的 `unlockCheckList.levelSaveKey`（后者只有"通关解锁"才有值，
水晶兑换/商店/抽奖/在线关卡的白卡全是空的 ⇒ 旧版会有 49 株掉进「其他」）。

| 组 | 判据（图鉴「获取方式」正则） | 0.30 命中 |
| --- | --- | --- |
| 第1~10章 | `通关冒险模式 (\d+)-(\d+)` | 13/13/13/13/13/13/18/18/18/18 = 150 |
| 杂交乐园 | `通关杂交乐园“XXX”关卡` | 3 |
| 在线关卡 | `在线关卡奖励 消耗 N 颗水晶兑换` | 42 |
| 商店购买 | `商店购买 “XXX”` | 1 |
| 抽奖盒子 | `通关抽奖盒子无尽版10回合` | 1 |
| 初始可用 | 图鉴无「获取方式」字段 | 1 |
| 其他 | 以上都不匹配（兜底，正常应为 0） | 0 |

**合计 198 株 = 游戏 0.30 的白卡总数**，全覆盖，无「其他」。

数据表在 `runtime_src/WhiteCardChapterSources.cs`（**由脚本生成，勿手改**）：

```csharp
ChapterGroupLines   // "组码|显示名|权重"   ← 15 条（A1..A10, HY, ON, SH, LT, BA, +未用到的预留组）
PlantChapterLines   // "植物键|组码",  // 中文名
ChapterOf(key)      // 键 → 组码（查不到返回 ""，调用方回落到 OT 兜底）
OrderedCodes        // 按权重排序的全部组码，决定菜单里的先后
LabelOf(code)       // 组码 → 显示名
```

菜单里每组显示成 `显示名（株数）`，`OT` 兜底组固定排在最后且仅在非空时出现。
点击某组：选卡界面写 `category["WCC_Chapter_<组码>"]` 后切分类；图鉴则走
`FilterAlmanacPage`（与标签筛选同一条路径）。

### 章节数据怎么来的（双路生成）

`tools/gen_chapter_sources.py` 合并两个数据源：

1. **xlsx（权威）** —— `data/白卡植物标签表.xlsx` 的 `图鉴说明` 工作表里
   「获取方式：…」那段文本。顺序 = 表格顺序。
2. **游戏解包（补齐）** —— 用于 xlsx 之后新版本新增的白卡：
   * 白卡名单：`Asset/Config/PacketBank/PacketBankResource.json` → `GeneralPlant.Category.White`
   * 获取方式：`Translate.zh.translation` 里该植物图鉴条目的「获取方式」文本（与 xlsx 同口径），
     拿不到时回退 packet `.tres` 的 `levelSaveKey`
   * 中文名：`Asset/Translate/Translate.csv`

⇒ 以后游戏再加新白卡，只要解包里带「获取方式」文本就会自动归到对应章节，**不用手改表**。
脚本需要工程根目录有 `unpack/`（0.30 解包），路径写在脚本顶部 `BASE`。

## 字号选择（第一级底部）

第一级面板底部有一行 **`字号  16 18 20 22 24 26`**（当前字号用**绿色高亮**标出）：

* 点任一数字 ⇒ 所有按钮立刻换成该字号，面板尺寸与列数**同步重算**
  （按钮最小宽 = `字号×4+24`，最小高 = `字号+20`）。
* 选择写入 `user://WhiteCardCategories.cfg`
  （即 `%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\WhiteCardCategories.cfg`）
  的 `[ui] button_font_size`，**重启游戏仍生效**。默认 18。

各字号在 874×942 窗口下「显示所有标签」页的推算（都能放下）：

| 字号 | 按钮最小 | 列 × 行 | 估算高度 |
| --- | --- | --- | --- |
| 16 | 88×36 | 6 × 11 | 602px |
| 18 | 96×38 | 6 × 11 | 624px |
| 20 | 104×40 | 6 × 11 | 646px |
| 22 | 112×42 | 6 × 11 | 668px |
| 24 | 120×44 | 6 × 11 | 690px |
| 26 | 128×46 | 6 × 11 | 712px |

## 自动识别（为新版本 / 其它 Mod 的白卡兜底）

烘焙表只覆盖制表时的 188 株（游戏 0.30 的白卡总数是 198）；**游戏更新或其它 Mod 新增的白卡不在表里** ⇒
由 `WhiteCardAutoTagger` 按表格「标签字典」的**判定依据**自动识别，仍判不出的进「其他」。

| 判据来源 | 覆盖的标签 | 实现 |
| --- | --- | --- |
| **配置标志位** `physiqueTypeFlags` | 坚果、花盆、地刺、猫尾、灯具、魔法、咖啡豆、辣椒、莲叶、机器、花瓶、不可穿透 | 位与判断，位值取自源码 `CHARACTER_PHYSIQUE_TYPE` |
| `elementFlags` | 冰、火 | `ICE=1` / `FIRE=2` |
| `plantGridType` | 水生（仅 WATER）、水陆（含 WATER） | 枚举遍历 |
| `sleepTime == "Day"` | 夜行 | 字符串比较 |
| `plantCover` 非空 | 仅底座 | — |
| `hitpoints >= 3000` | 坚果（表格对「高韧性」的补充口径） | — |
| **组件集** `CharacterComponentSet` | 一次性、近战、发射、反击、火炬、地雷、磁力、保护伞、吹飞、窝瓜、成长、缠绕、大嘴花、墓吞、胆小、花盆、滚动攻击、吸金/产资源、产阳光 | 见下 |
| **名称关键词**（植物键小写） | 系列类（豌豆/向日葵/寒冰/大蒜/樱桃/…）、攻击方式近似（投抛/猫尾/射击/穿透） | 27 条规则 |

### 组件集是怎么在运行时拿到的

实测的导出结构：角色场景 `.../Scene/TowerDefensePlant<X>.tscn` 的 `ext_resource`
**直接引用** `.../Scene/TowerDefensePlant<X>ComponentSet.tres`（同目录同名）。所以：

```csharp
PackedScene scene = ResourceManager.Instance.GetCharacterScene(pc.name);   // pc.name 实测 = 植物键
string setPath = scene.ResourcePath.Replace(".tscn", "ComponentSet.tres");
CharacterComponentSet cs = ResourceLoader.Load(setPath) as CharacterComponentSet;
IReadOnlyList<CharacterComponentDefinition> defs = cs.GetFlattenedDefinitions();
// 逐个 def.ComponentTypeId；ProduceComponentDefinition 另读 produceType
```

⚠️ 用 `GetFlattenedDefinitions()` 而不是 `Components` —— 组件集支持 `ParentSet` 继承，
直接读 `Components` 会漏掉继承来的组件。

⚠️ **只对表外的卡跑自动识别**。自动识别要加载该角色的组件集资源，
188 张卡全跑一遍会在进选卡界面时集中做 188 次资源加载 ⇒ 明显卡顿；
而烘焙表本就是这 188 株的权威快照。若游戏更新改了**已有**卡的标签，
应**重新导出表格并重生成烘焙表**，而不是依赖这里兜底。

### 未能自动识别的标签（诚实说明）

| 标签 | 为什么 |
| --- | --- |
| 射击 / 投抛 / 穿透 / 猫尾 / 后向弹道 / 滚动弹道 | 精确判据要钻进 `FireComponentDefinition` 内嵌两层子资源的 `TowerDefenseProjectileCreateData.fireMethodFlags`；为避免运行时深挖嵌套资源，改用**名称关键词近似**。这 6 个标签在 188 株白卡上已全部烘焙，仅影响表外新卡。 |
| 魅惑 | 表格判据是"名称含魅惑"或脚本调用 `zombie.Hypnoses()`（脚本逻辑无法在选卡期读到）。名称关键词里没有独立规则，表外卡可能漏标。 |
| 海兵菇 / 治愈 / 击退 / 弹幕 | 表格标注为**人工指定**（源码无可判定信号），表外卡无法自动识别。 |

## 仓库结构

```
WhiteCardCategories/
├── mod.json                     ← Mod 清单
├── build_mod.py                 ← 编译 + 打包 + 装机
├── runtime_src/                 ← C# 源码
│   ├── WhiteCardCategoriesEntry.cs    主入口（UI + 注入 + 二级菜单）
│   ├── WhiteCardAutoTagger.cs         运行时自动识别标签
│   ├── WhiteCardCategoriesTable.cs    ★ 由表格生成的烘焙表（勿手改）
│   └── WhiteCardChapterSources.cs     ★ 由解包+xlsx生成的章节表（勿手改）
├── data/
│   ├── 白卡植物标签表.xlsx       ★ 标签体系的**原始数据源**
│   └── whitecard_tags.json      由表格导出的中间数据（可重建）
├── tools/
│   ├── extract_whitecard_tags.py     xlsx → json（含校验）
│   ├── gen_whitecard_table.py        json → WhiteCardCategoriesTable.cs
│   ├── gen_chapter_sources.py        xlsx + 解包 → WhiteCardChapterSources.cs
│   └── verify_table_roundtrip.py     ★ 回环校验：C# 表 vs xlsx 逐项比对
├── Runtime/ModAssembly.dll      ← 打包用（编译产物改名而来）
└── dist/WhiteCardCategories.pmod ← 成品
```

## 构建与再生成

```powershell
# 1) 改了表格 ⇒ 重新导出 + 重生成烘焙表 + 校验
python tools\extract_whitecard_tags.py     # xlsx -> data/whitecard_tags.json（当场校验）
python tools\gen_whitecard_table.py        # json -> runtime_src/WhiteCardCategoriesTable.cs
python tools\verify_table_roundtrip.py     # 回环校验：C# 表 vs 表格逐项比对

# 1b) 游戏更新了 ⇒ 重生成章节表（需要工程根目录有 unpack/）
python tools\gen_chapter_sources.py        # -> runtime_src/WhiteCardChapterSources.cs

# 2) 编译 + 打包 + 装机
python build_mod.py --install
```

三个脚本的路径都是**相对脚本自身**的，换机器/换目录都能直接跑，只要保持上面这个目录结构。

* `extract_whitecard_tags.py` 会**当场校验**：植物键是否齐全、标签是否全在字典里、分组是否只有那 5 个。
* `verify_table_roundtrip.py` 是**必须跑的**：烘焙表是生成的，一旦生成脚本出错（漏行、串行、分隔符处理错），
  表现只会是"某株植物少了个标签"，肉眼极难发现。它逐项比对大类顺序、每个大类的标签、每株植物的标签，
  并检查有没有游离标签。退出码 0 才算通过。

## 落点与切换机制（读源码核实）

`TowerDefenseInGamePacketBank.tscn` 的 `Translate/CardSort` 是右侧分类面板，
四个自带按钮 `CardMod(62) / CardItem(137) / CardGraveStone(212) / CardZombie(287)`，
竖直间距 **75px** ⇒ 「僵尸卡」下面是 **y=362**，就是本 Mod 按钮的位置。

切换待选区走**游戏原生路径** `TowerDefenseBattleFeaturePacketBank.CategoryChoose(键)`：
它读 `packetBankData.category[键]` 重建卡池。所以本 Mod 只需把标签桶**注入**
`packetBankData.category["WCC_<标签>"]`（前缀避免与游戏键撞车）再调用它。
`SetPacketBankData()` 每次进关卡都会重建 `packetBankData` ⇒ 注入按实例 ID 重做。

**自制关卡的编辑卡牌库**（`LevelEditorPacketBank`）同样支持，代码完全共用。

**图鉴植物页**（v2.13）走的是另一条路：`Almanac.plantPacketBank` 是
`XWModContentCatalog.WithPlants()` 深拷贝出来的**私有副本**，图鉴没有 `CategoryChoose`。
本 Mod 的做法是把**当前页那个 category 键**的数组临时替换成筛选结果，再调 `Almanac.InitPlant()` ——
`InitPlant()` 是公开方法且只读 `plantPacketBank.category[plantCategoryId]`，
所以换数组内容就是官方支持的筛选入口，`plantCategoryId` 一动不动（翻页/页数/排序语义全保持）。
详见上文「植物图鉴页」一节。

图鉴实例每次打开都是**新对象**（`DialogManager.DialogCreate` 现造现挂、关闭 `QueueFree`），
所以按实例 ID 判活：`_tree.Root.FindChild("Almanac", true, false)` 限频扫描（每 5 帧一次），
换实例就清缓存重建按钮。⚠️ 图鉴在**主菜单/暂停菜单**也能开（`MainMenu.AlmanacButtonPressed`），
那时 `TowerDefenseManager.Instance` 可能是 null ⇒ 图鉴分支必须写在 `OnFrame` 的
`TowerDefenseManager` 早退守卫**之前**。

## 已知坑

1. **`PacketCategoryButton` 没有 `Pressed`** —— 点击链是
   `SpriteBrightButton.OnPressed → ButtonPressed() → OnChoose?.Invoke(category)`。
   自定义按钮要订阅 **`OnChoose`**（`ChooseEventHandler(string)`），不是 `Pressed`。
2. **手写 csproj ⇒ 没有 Godot 源生成器** ⇒ 入口类的自定义 `_Process` 永远不会被调用，
   必须挂 `SceneTree` 的 `Connect("process_frame", …)`。
3. **`Callable` 不能隐式转 `Action`**（只有反向可以）⇒ 用 `Connect(名字, callable)`。
4. **`Node.Name` 是 `StringName`** ⇒ 比较必须 `Name.ToString()`。
5. **包内绝不能有 `.cs`** —— ModLoader 可执行文件白名单会整包拒收。

## 诊断

源码顶部：

```csharp
private static readonly bool EnableLog = false;   // 置 true 重新构建即可看注入详情
```

会打出「已注入白卡标签：N 大类 / M 个非空标签 / X 张白卡（其中表外自动识别 Y 张）；兜底「其他」Z 张」。

## 变更记录

* **v2.14.1** —— 适配游戏 0.30.0：新增的第 10 章 10-11～10-20 共 10 株白卡（魔法睡莲 /
  寒冰海草 / 魔法大嘴花 / 洋流高坚果 / 精灵胆小菇 / 魔法海蘑菇 / 魔法大喷菇 / 魔法猫尾草 /
  巫师南瓜 / 魔法石坚果）并入「第10章」⇒ 章节分类 188 → **198 株**。章节表改为
  「xlsx + 解包」双路生成，以后新白卡自动归类。
* **v2.14.0** —— 修「章节分类不全」（玩家反馈）：数据源从 `.tres` 的 `levelSaveKey`
  换成图鉴「获取方式」文本，白卡全部有归属，不再有 49 株掉进「其他」；新增
  在线关卡 / 商店购买 / 杂交乐园 / 抽奖盒子 / 初始可用 五组；入口改名「按获取章节」。
* **v2.13.0** —— 图鉴植物页左上角新增同款「白卡分类」按钮 + 标签筛选；新增
  `showInBattleSelect` / `showInAlmanac` 两个显示开关。
* v2.12.3 —— 修「点标签后是空池」：标签桶改为**点击时**按烘焙表现算，而不是注入阶段预写。
* v2.3.0 —— 表格标签体系（5 大类 / 71 标签 / 188 株）、二级菜单、字号选择。

## 历史文件

* `分类清单-白卡最终.json`、`方案报告-白卡分类.md` —— **v1 的单层 23 类别方案**，
  v2 已改为表格标签体系，这两个文件仅作存档，与当前实现无关。
