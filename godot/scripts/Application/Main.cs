using System;
using System.Linq;
using Godot;
using Inotia4.Domain;
using Inotia4.Ui;

namespace Inotia4.Application;

public partial class Main : Node
{
    private TitleScene? _title;
    private ClassSelectScene? _classSelect;
    private Scenes.WorldScene? _world;
    private int _shotCountdown = -1;
    private string _shotName = "title";

    /// <summary>新游戏落在哪个手动槽：职业选完才开局，所以先记在这里。</summary>
    private int? _pendingSlot;

    public override void _Ready()
    {
        ApplyRefreshRate();

        var args = OS.GetCmdlineUserArgs();

        // --validatecontent：把内容全量读一遍再跑关系校验，跑完就退。平时不进世界时不做这件事：
        // 全量校验要读几份大目录，运行时按需加载，不该为校验买单。
        if (Array.IndexOf(args, "--validatecontent") >= 0)
        {
            RunContentValidation();
            return;
        }

        var titlePage = ArgValue(args, "--titlepage");
        var titlePlay = ArgValue(args, "--titleplay");

        if (titlePage is not null || titlePlay is not null)
        {
            var title = new TitleScene
            {
                InitialPage = titlePage,
                AutoPlay = titlePlay,
            };
            title.StartRequested = OnTitleStart;
            AddChild(title);
            _title = title;

            var shotIndex = Array.IndexOf(args, "--shot");
            if (shotIndex >= 0)
            {
                _shotCountdown = shotIndex + 1 < args.Length && int.TryParse(args[shotIndex + 1], out var frames)
                    ? frames
                    : 150;
                SetProcess(true);
            }

            return;
        }

        if (Array.IndexOf(args, "--classselect") >= 0)
        {
            ShowClassSelect(null, ArgValue(args, "--classselect"));
            var shotIndex = Array.IndexOf(args, "--shot");
            if (shotIndex >= 0 && Array.IndexOf(args, "--classstart") < 0)
            {
                _shotName = "class-select";
                _shotCountdown = shotIndex + 1 < args.Length && int.TryParse(args[shotIndex + 1], out var frames)
                    ? frames
                    : 150;
                SetProcess(true);
            }

            if (Array.IndexOf(args, "--classstart") >= 0)
            {
                var classId = ArgValue(args, "--classselect");
                CallDeferred(nameof(StartChosen), classId ?? string.Empty);
            }

            return;
        }

        if (args.Length > 0)
        {
            StartWorld(null, null, null);
            return;
        }

        ShowTitle();
    }

    public void ShowTitle()
    {
        if (_world is not null)
        {
            _world.QueueFree();
            _world = null;
        }

        if (_classSelect is not null)
        {
            _classSelect.QueueFree();
            _classSelect = null;
        }

        if (_title is not null) return;
        var title = new TitleScene();
        title.StartRequested = OnTitleStart;
        AddChild(title);
        _title = title;
    }

    private void OnTitleStart(SaveSlotRef? resume, int? newSlot)
    {
        if (resume is not null)
        {
            StartWorld(resume, newSlot, null);
            return;
        }

        // --titleplay new 的无头链路：标题页自动按了「开始游戏」，职业选择也跟着自动开局。
        // 注意这里不能读 _title.AutoPlay——标题页是在自己的 _Ready 里回调过来的，
        // 那时 Main 还没把它记进字段。
        ShowClassSelect(newSlot, null, autoPlay: ArgValue(OS.GetCmdlineUserArgs(), "--titleplay") == "new");
    }

    private void ShowClassSelect(int? newSlot, string? presetClass, bool autoPlay = false)
    {
        if (_title is not null)
        {
            _title.QueueFree();
            _title = null;
        }

        _pendingSlot = newSlot;
        var scene = new ClassSelectScene
        {
            InitialClass = presetClass,
            BackRequested = ShowTitle,
            ClassChosen = StartChosen,
            AutoPlayClass = autoPlay ? presetClass ?? string.Empty : null,
        };
        AddChild(scene);
        _classSelect = scene;
    }

    private void StartChosen(string classId) => StartWorld(null, _pendingSlot, classId);

    private void StartWorld(SaveSlotRef? resume, int? newSlot, string? classId)
    {
        if (_title is not null)
        {
            _title.QueueFree();
            _title = null;
        }

        if (_classSelect is not null)
        {
            _classSelect.QueueFree();
            _classSelect = null;
        }

        if (_world is not null)
        {
            _world.TitleResume = resume;
            _world.TitleNewSlot = newSlot;
            _world.TitleClassId = classId;
            return;
        }

        var world = new Scenes.WorldScene();
        world.TitleResume = resume;
        world.TitleNewSlot = newSlot;
        world.TitleClassId = classId;
        world.TitleRequest = ShowTitle;
        AddChild(world);
        _world = world;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// 让渲染帧率跟着屏幕刷新率走。
    ///
    /// Godot 在 Android 上默认把 vsync 间隔钉在 1，而系统给普通应用的显示模式是 60Hz，
    /// 于是 144Hz 屏上也只出 60 帧（这是默认行为，不是性能不够）。这里做三件事：
    /// 安卓上关掉 vsync（桌面不动，桌面开着才不撕裂）、请求系统切到高刷模式、
    /// 再把 <c>Engine.MaxFps</c> 对齐到屏幕报的刷新率——关掉 vsync 又完全不限帧会跑满
    /// GPU、白烧电，限在刷新率上才是「画面顺、电也够用」。
    ///
    /// 屏幕报多少是系统说了算：系统坚持只给 60Hz 显示模式的话，这里读到 60 就还是 60。
    /// 所以这几行日志要留着——出问题时先看它们。
    /// </summary>
    private void ApplyRefreshRate()
    {
        if (OS.GetName() == "Android")
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            RequestHighRefreshRate();
        }

        SyncMaxFps();

        if (OS.GetName() == "Android")
            GetTree().CreateTimer(1.5).Timeout += () => SyncMaxFps();
    }

    /// <summary>
    /// 设置页「高帧率」开关：开＝跟着屏幕刷新率（安卓上关 vsync 并请求高刷模式），
    /// 关＝回到 60 并重新打开 vsync。桌面也照这个走——桌面关掉就是 60，方便省电或录屏。
    ///
    /// 关掉时把 vsync 打开是刻意的：只把 MaxFps 调到 60 而 vsync 仍关着，画面会出现
    /// 60fps 与 144Hz 之间的追帧抖动；交给 vsync 卡住才是稳的 60。
    ///
    /// 返回一句能直接给玩家看的结论（设置页会把它弹在屏幕上）：到底读到了多少 Hz。
    /// 安卓上切显示模式是异步的，所以 1.5 秒后会再对一次上限——不然刚请求完就按旧的 60
    /// 把 MaxFps 钉死，等于白请求（运行时开关曾经就是这么失效的）。
    /// </summary>
    public static string ApplyHighRefresh(bool high)
    {
        if (!high)
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled);
            Engine.MaxFps = 60;
            GD.Print("[fps] 高帧率关闭：回到 60 帧（vsync 已开）");
            return "高帧率已关：上限 60 帧";
        }

        var request = string.Empty;
        if (OS.GetName() == "Android")
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            request = RequestHighRefreshRate();
        }

        var hz = SyncMaxFps();

        if ((Engine.GetMainLoop() as SceneTree)?.CreateTimer(1.5) is { } timer)
            timer.Timeout += () =>
            {
                var late = SyncMaxFps();
                GD.Print($"[fps] 1.5 秒后复查：屏幕 {late}Hz");
            };

        return hz > 60
            ? $"屏幕 {hz}Hz：上限 {Engine.MaxFps} 帧{request}"
            : $"屏幕只报到 {hz}Hz：上限 {Engine.MaxFps} 帧（系统可能没放开高刷）{request}";
    }

    private static int SyncMaxFps()
    {
        var hz = DisplayServer.ScreenGetRefreshRate(DisplayServer.WindowGetCurrentScreen());
        if (!float.IsFinite(hz) || hz <= 0f) hz = 60f;

        Engine.MaxFps = Mathf.RoundToInt(hz);
        GD.Print($"[fps] 屏幕 {hz:F0}Hz → 上限 {Engine.MaxFps} 帧（{OS.GetName()}）");
        return Engine.MaxFps;
    }

    /// <summary>
    /// 让系统把这块屏切到它能给的最高刷新率。
    ///
    /// Godot 4.5 在安卓上没有请求显示模式的代码路径（只有 iOS 有
    /// <c>display/window/ios/allow_high_refresh_rate</c>），所以只能自己调
    /// Android 的 <c>Window.setFrameRate()</c>（API 30+）：系统会从屏幕支持的
    /// 模式里挑一个最接近的，144Hz 屏就是 144。
    ///
    /// 全程 try/catch：拿不到 AndroidRuntime 单例、或者设备是 API 30 以下，
    /// 都只是少一次请求，游戏照常跑——绝不能为了这点平滑度在启动时崩掉。
    /// </summary>
    private static string RequestHighRefreshRate()
    {
        try
        {
            var runtime = Engine.GetSingleton("AndroidRuntime");
            if (runtime is null)
            {
                GD.Print("[fps] 没有 AndroidRuntime 单例，跳过高刷请求");
                return "（没有 AndroidRuntime，跳过了高刷请求）";
            }

            var activity = runtime.Call("getActivity").AsGodotObject();
            var window = activity?.Call("getWindow").AsGodotObject();
            if (window is null)
            {
                GD.Print("[fps] 拿不到 Activity/Window，跳过高刷请求");
                return "（拿不到 Activity/Window，跳过了高刷请求）";
            }

            // FRAME_RATE_COMPATIBILITY_FIXED_SOURCE = 1：告诉系统「我就要这个帧率，
            // 别为了省电偷偷降，也别做 frame-rate 插值」。数值给高一点由系统取最近的模式。
            window.Call("setFrameRate", 144f, 1);
            GD.Print("[fps] 已请求高刷模式（setFrameRate 144）");
            return "";
        }
        catch (Exception error)
        {
            GD.Print($"[fps] 请求高刷失败（不影响游戏）：{error.Message}");
            return $"（请求高刷失败：{error.Message}）";
        }
    }

    private void RunContentValidation()
    {
        try
        {
            var report = new ContentLoader().ValidateAll();
            GD.Print($"[content] 校验通过：物品 {report.Items}、技能 {report.Skills}（{report.Classes} 个职业）、" +
                     $"商店 {report.Shops}、地图 {report.Maps}、任务 {report.Quests}、剧本 {report.Scripts}、" +
                     $"伙伴 {report.Companions}、换装外观 {report.Outfits}、音效 {report.Sounds}；" +
                     $"剧情图 {report.StoryMaps} 张，走查节点 {report.StoryNodes}／舞台 {report.Stages}／" +
                     $"走位 {report.Walks}；耗时 {report.Elapsed.TotalMilliseconds:F0} ms");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr($"[content] 校验失败：{error.Message}");
            GetTree().Quit(1);
        }
    }

    public override void _Process(double delta)
    {
        if (_shotCountdown > 0 && --_shotCountdown == 0) SaveShot();
    }

    private void SaveShot()
    {
        var directory = ProjectSettings.GlobalizePath("res://.shots");
        DirAccess.MakeDirRecursiveAbsolute(directory);

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("[shot] 无头模式下拿不到视口画面，跳过截图");
            GetTree().Quit();
            return;
        }

        var image = GetViewport().GetTexture().GetImage();
        var path = System.IO.Path.Combine(directory, $"{_shotName}.png");
        image.SavePng(path);
        GD.Print($"[shot] {path}");
        GetTree().Quit();
    }
}
