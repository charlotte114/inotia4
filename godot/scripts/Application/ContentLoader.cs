using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inotia4.Domain;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;
using Inotia4.Domain.Story;

namespace Inotia4.Application;

/// <summary>
/// 读取并缓存共用的内容 JSON。反序列化只建立模型，不能替代字段和跨目录关系校验；
/// 关系校验有一个显式入口 <see cref="ValidateAll"/>（<c>--validatecontent</c>），
/// 不在加载路径上自动跑——原因见那一段注释；尚未对应的校验边界见迁移自检清单。
/// </summary>
public sealed class ContentLoader
{
	private static readonly JsonSerializerOptions Options = CreateOptions();

	private static JsonSerializerOptions CreateOptions()
	{
		var options = new JsonSerializerOptions
		{
			AllowTrailingCommas = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
		};
		Inotia4.Domain.Combat.SkillActionJson.Configure(options);
		return options;
	}

	private WorldIndex? _world;
	private BattleContent? _battle;
	private SkillCatalog? _skills;
	private CharacterStatsCatalog? _stats;
	private ItemCatalog? _items;
	private WardrobeCatalog? _wardrobe;
	private CampaignContent? _campaign;
	private CraftingCatalog? _crafting;
	private FusionCatalog? _fusion;
	private ShopCatalog? _shops;
	private MenuPanelsCatalog? _menuPanels;
	private ControlSkinCatalog? _controlSkin;

	private SpringCatalog? _springs;
	private StatusHudSkin? _statusHud;
	private StoryInterfaceAssets? _interface;
	private StoryClassCatalog? _storyClasses;
	private CompanionCatalog? _companions;
	private AudioCatalog? _audio;
	private NpcFunctionCatalog? _npcFunctions;
	private readonly Dictionary<string, MapDefinition> _maps = new();

	public WorldIndex World => _world ??= Read<WorldIndex>("content/world.json");

	public NpcFunctionCatalog NpcFunctions
	{
		get
		{
			if (_npcFunctions is not null) return _npcFunctions;
			var path = ContentRoot.Resolve("content/npc-functions.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/npc-functions.json", path);
			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});
			return _npcFunctions = NpcFunctionCatalog.Parse(document.RootElement);
		}
	}
	public CharacterStatsCatalog Stats => _stats ??= Read<CharacterStatsCatalog>("content/character-stats.json");

	public ItemCatalog Items => _items ??= Read<ItemCatalog>("content/items.json");

	public WardrobeCatalog Wardrobe => _wardrobe ??= Read<WardrobeCatalog>("content/wardrobe.json");

	/// <summary>
	/// 剧情内容（9.5MB：693 段脚本、505 个任务）。整包解析约一百多毫秒，
	/// 所以只在要开新游戏或进剧本时加载。
	/// </summary>
	public CampaignContent Campaign => _campaign ??= Read<CampaignContent>("content/campaign.json");

	public StoryDebugCatalog StoryDebug
	{
		get
		{
			var path = ContentRoot.Resolve("content/debug/start.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/debug/start.json", path);

			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});

			return Inotia4.Domain.Story.StoryDebug.Parse(document.RootElement);
		}
	}

	/// <summary>
	/// 战斗内容（4.6MB）。只在需要时加载——整包解析要一两秒，
	/// 普通跑图不该为它买单。
	/// </summary>
	public BattleContent Battle => _battle ??= Read<BattleContent>("content/battle.json");

	public SkillCatalog Skills
	{
		get
		{
			if (_skills is not null) return _skills;

			if (_battle is not null)
			{
				var borrowed = SkillCatalog.FromBattle(_battle);
				borrowed.Prepare();
				return _skills = borrowed;
			}

			var catalog = Read<SkillCatalog>("content/battle.json");
			catalog.Prepare();
			return _skills = catalog;
		}
	}

	public CraftingCatalog Crafting
	{
		get
		{
			if (_crafting is not null) return _crafting;

			var path = ContentRoot.Resolve("content/crafting.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/crafting.json", path);

			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});

			_crafting = CraftingCatalog.Parse(document.RootElement);
			_crafting.Validate(Items);
			return _crafting;
		}
	}

	public FusionCatalog Fusion
	{
		get
		{
			if (_fusion is not null) return _fusion;

			var path = ContentRoot.Resolve("content/fusion.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/fusion.json", path);

			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});

			_fusion = FusionCatalog.Parse(document.RootElement);
			_fusion.Validate(Items);
			return _fusion;
		}
	}

	/// <summary>
	/// 商店表（18 家店 + 662 条装备模板，约 1MB）。档位变体的派生要读角色数值表，
	/// 所以解析时把 <see cref="Stats"/> 一起带过去。只在第一次走进商店时解析。
	/// </summary>
	public ShopCatalog Shops
	{
		get
		{
			if (_shops is not null) return _shops;

			var path = ContentRoot.Resolve("content/shops.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/shops.json", path);

			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});

			return _shops = ShopCatalog.Parse(document.RootElement, Stats);
		}
	}

	public MenuPanelsCatalog MenuPanels => _menuPanels ??= Read<MenuPanelsCatalog>("content/menu-panels.json");

	public Drops.DropCatalog Drops
	{
		get
		{
			if (_drops is not null) return _drops;

			var path = ContentRoot.Resolve("content/drops.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/drops.json", path);

			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});

			return _drops = Inotia4.Domain.Drops.ParseDrops(
				document.RootElement, Items, Battle.Enemies.Select(enemy => enemy.Id).ToList());
		}
	}

	private Drops.DropCatalog? _drops;

	public SpringCatalog Springs
	{
		get
		{
			if (_springs is not null) return _springs;

			var path = ContentRoot.Resolve("content/springs.json");
			if (!File.Exists(path)) throw new FileNotFoundException("找不到内容文件：content/springs.json", path);

			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});

			return _springs = SpringCatalog.Parse(document.RootElement);
		}
	}

	public ControlSkinCatalog ControlSkin => _controlSkin ??= Read<ControlSkinCatalog>("content/control-skin.json");

	public StatusHudSkin StatusHud => _statusHud ??= Read<StatusHudSkin>("content/status-hud.json");

	public StoryInterfaceAssets Interface => _interface ??= Read<StoryInterfaceAssets>("content/story-interface.json");

	public StoryClassCatalog StoryClasses => _storyClasses ??= Read<StoryClassCatalog>("content/story-classes.json");

	public CompanionCatalog Companions =>
		_companions ??= Read<CompanionCatalog>("content/companions.json");

	public AudioCatalog Audio => _audio ??= Read<AudioCatalog>("content/audio.json");

	public MapDefinition LoadMap(string mapId)
	{
		if (_maps.TryGetValue(mapId, out var cached)) return cached;

		var entry = World.Maps.FirstOrDefault(map => map.Id == mapId)
			?? throw new KeyNotFoundException($"world.json 里没有地图 {mapId}");

		var map = Read<MapDefinition>(entry.File);
		_maps[mapId] = map;
		return map;
	}

	/// <summary>
	/// 全量内容校验：把每个目录都建出来，先跑 <see cref="ContentValidation"/> 的跨目录关系校验，
	/// 再跑剧情自己的两层（<see cref="StoryValidation"/> 的引用、<see cref="StoryPerformanceValidation"/> 的演出）。
	///
	/// 为什么不在加载时自动跑：这些校验都是「跨文件对不上」的检查，必须同时持有
	/// 物品、技能、商店、数值表、伙伴、剧情与菜单几份目录；战斗包 4.6MB、剧情包 9.5MB，
	/// 按需加载正是为了不让跑图买单（见各属性上的注释）。这些检查也只在构建工具里跑，
	/// 不是运行时的事。
	///
	/// 所以这里做成一个显式入口：<c>--validatecontent</c> 启动一次、跑完就退，
	/// 内容和校验器改动后用它兜底。失败会抛出校验器给出的第一条原因。
	/// </summary>
	public ContentValidationReport ValidateAll()
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();

		var items = Items;
		var skills = Skills;
		var shops = Shops;
		var world = World;
		var stats = Stats;
		var campaign = Campaign;
		var companions = Companions;
		var menu = MenuPanels;
		var face = Interface;
		var wardrobe = Wardrobe;
		var audio = Audio;
		var battle = Battle;

		WorldShape.ValidateWorld(ReadRaw("content/world.json"));
		CatalogShape.ValidateItems(ReadRaw("content/items.json"));
		CatalogShape.ValidateShopCatalog(ReadRaw("content/shops.json"));
		CatalogShape.ValidateCompanionCatalog(ReadRaw("content/companions.json"));
		CatalogShape.ValidateStoryClassCatalog(ReadRaw("content/story-classes.json"));
		UiShape.ValidateCharacterStats(ReadRaw("content/character-stats.json"));
		UiShape.ValidateMenuPanels(ReadRaw("content/menu-panels.json"));
		UiShape.ValidateStatusHud(ReadRaw("content/status-hud.json"));
		UiShape.ValidateControlSkin(ReadRaw("content/control-skin.json"));
		UiShape.ValidateStoryInterface(ReadRaw("content/story-interface.json"));
		BattleShape.ValidateBattle(ReadRaw("content/battle.json"));
		WardrobeShape.ValidateWardrobe(ReadRaw("content/wardrobe.json"));
		CampaignShape.ValidateCampaign(ReadRaw("content/campaign.json"));
		ContentValidation.ValidateItemSkills(items, skills);
		ContentValidation.ValidateShops(shops, items, world);
		ContentValidation.ValidateCharacterStats(stats, items, skills);
		ContentValidation.ValidateWardrobe(wardrobe, items, skills, battle);
		ContentValidation.ValidateStoryItems(campaign, items);
		ContentValidation.ValidateCompanions(companions, world, skills, stats, campaign);
		ContentValidation.ValidateStoryPortraits(world, face);
		ContentValidation.ValidateMenuClasses(menu, skills);
		ContentValidation.ValidateAudio(audio, items, world, battle, skills, wardrobe);
		ContentValidation.ValidateMaps(world, ReadMapForValidation, battle, campaign);

		// 剧情引用：只加载剧情真正提到的图（269 张）。与上面逐张现读的 ValidateMaps 分开，
		// 因为这些图要留着反复查（舞台落位、摆放归属、遭遇坐标），而全量 416 张不必同时在场。
		var storyMaps = new Dictionary<string, MapDefinition>(StringComparer.Ordinal);
		foreach (var id in StoryValidation.ReferenceMaps(campaign))
			storyMaps[id] = ReadMapUncached(id);
		StoryValidation.ValidateReferences(campaign, world, battle, storyMaps, items);

		var performance = StoryPerformanceValidation.Validate(campaign, world, audio, storyMaps);

		return new ContentValidationReport(
			Items: items.Items.Count,
			Skills: skills.Skills.Count,
			Classes: skills.Classes.Length,
			Shops: shops.Shops.Count,
			Maps: world.Maps.Length,
			Quests: campaign.Quests.Length,
			Scripts: campaign.Scripts.Count,
			Companions: companions.Definitions.Count,
			Outfits: wardrobe.Equipment.Count,
			Sounds: audio.Effects.Count,
			StoryMaps: storyMaps.Count,
			StoryNodes: performance.Nodes,
			Stages: performance.Stages,
			Walks: performance.Walks,
			Elapsed: watch.Elapsed);
	}

	private MapDefinition ReadMapUncached(string mapId)
	{
		var entry = World.Maps.FirstOrDefault(map => map.Id == mapId)
			?? throw new KeyNotFoundException($"world.json 里没有地图 {mapId}");
		return Read<MapDefinition>(entry.File);
	}

	/// <summary>
	/// 全量校验用的地图读取：同一份文本先做形状校验、再反序列化成模型。
	/// 与运行时的 <see cref="ReadMapUncached"/> 分开——形状校验是构建期工具的事，
	/// 玩家换图不该为它买单（这类校验只在构建期跑）。
	/// </summary>
	private MapDefinition ReadMapForValidation(string mapId)
	{
		var entry = World.Maps.FirstOrDefault(map => map.Id == mapId)
			?? throw new KeyNotFoundException($"world.json 里没有地图 {mapId}");
		var node = ReadRaw(entry.File);
		WorldShape.ValidateMap(node, mapId);
		return node.Deserialize<MapDefinition>(Options)
			?? throw new InvalidDataException($"内容文件解析结果为空：{entry.File}");
	}

	private static JsonNode ReadRaw(string relative)
	{
		var path = ContentRoot.Resolve(relative);
		if (!File.Exists(path)) throw new FileNotFoundException($"找不到内容文件：{relative}", path);
		return JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip,
		}) ?? throw new InvalidDataException($"内容文件解析结果为空：{relative}");
	}

	public IReadOnlyDictionary<string, string> PropFiles()
	{
		var files = new Dictionary<string, string>(World.Props.Length);
		foreach (var prop in World.Props) files[prop.Key] = prop.File;
		return files;
	}

	private static T Read<T>(string relative) where T : class
	{
		var path = ContentRoot.Resolve(relative);
		if (!File.Exists(path)) throw new FileNotFoundException($"找不到内容文件：{relative}", path);

		return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
			?? throw new InvalidDataException($"内容文件解析结果为空：{relative}");
	}
}

public sealed record ContentValidationReport(
	int Items, int Skills, int Classes, int Shops, int Maps, int Quests, int Scripts, int Companions,
	int Outfits, int Sounds, int StoryMaps, int StoryNodes, int Stages, int Walks, TimeSpan Elapsed);
