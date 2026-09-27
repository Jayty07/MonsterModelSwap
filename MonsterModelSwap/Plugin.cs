using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MonsterModelSwap.Data;
using MonsterModelSwap.Services;
using MonsterModelSwap.Windows;

namespace MonsterModelSwap;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mms";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("MonsterModelSwap");
    private readonly Configuration config;
    private readonly ModelDatabase database;
    private readonly ModelSwapService swap;
    private readonly AnimationService anim;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        database = new ModelDatabase(PluginInterface, DataManager, Log);
        swap = new ModelSwapService(Framework, ClientState, Condition, ObjectTable, Log, config, new CameraScaleService(Log, ObjectTable, GameInterop));
        anim = new AnimationService(DataManager, KeyState, Log, config, swap, database);
        anim.BindsChanged += SaveConfig;
        mainWindow = new MainWindow(PluginInterface, config, database, swap, anim);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Monster Model Swap window. /mms apply <id> | revert | persist [on|off] | height <x> | camoffset <y> | anim <timeline id> | animloop <id|0> | animstop | camdebug",
        });

        Framework.Update += OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        if (config.OpenOnStartup) mainWindow.IsOpen = true;
        if (config.ApplyOnLogin && config.SelectedModelId > 0 && ClientState.IsLoggedIn)
            Framework.RunOnFrameworkThread(() => swap.Apply(config.SelectedModelId, config.Height));
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        CommandManager.RemoveHandler(Command);
        windowSystem.RemoveAllWindows();
        Framework.Update -= OnFrameworkUpdate;
        anim.BindsChanged -= SaveConfig;
        anim.Dispose();

        try
        {
            Framework.RunOnFrameworkThread(() =>
            {
                if (swap.Active) swap.Revert();
            }).Wait(2000);
        }
        catch (System.Exception e)
        {
            Log.Warning(e, "Revert on unload failed");
        }

        swap.Dispose();
    }

    private void ToggleMainUi() => mainWindow.Toggle();

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void OnFrameworkUpdate(IFramework _) => anim.Tick();

    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            ToggleMainUi();
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "apply":
                if (parts.Length > 1 && int.TryParse(parts[1], out var id) && id > 0)
                {
                    config.SelectedModelId = id;
                    PluginInterface.SavePluginConfig(config);
                    swap.Apply(id, config.Height);
                }
                else if (config.SelectedModelId > 0)
                {
                    swap.Apply(config.SelectedModelId, config.Height);
                }
                else
                {
                    Log.Warning("Usage: /mms apply <ModelChara id>");
                }
                break;

            case "revert":
                swap.Revert();
                break;

            case "headheight":
                if (parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var hh))
                {
                    config.HeadHeight = System.Math.Clamp(hh, 0f, 10f);
                    PluginInterface.SavePluginConfig(config);
                }
                else
                {
                    Log.Warning($"Usage: /mms headheight <metres, 0 = auto>. Game's current value: {swap.CurrentHeadHeight:F2}");
                }
                break;

            case "height":
                if (parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var h))
                {
                    config.Height = h;
                    PluginInterface.SavePluginConfig(config);
                    swap.SetHeight(h);
                }
                else
                {
                    Log.Warning("Usage: /mms height <multiplier, e.g. 1.5>");
                }
                break;

            case "anim":
                if (parts.Length > 1 && ushort.TryParse(parts[1], out var tl) && tl > 0)
                    anim.Play(tl);
                else
                    Log.Warning("Usage: /mms anim <ActionTimeline id>");
                break;

            case "animloop":
                if (parts.Length > 1 && ushort.TryParse(parts[1], out var loop))
                    anim.SetLoop(loop);
                else
                    Log.Warning("Usage: /mms animloop <ActionTimeline id | 0 to clear>");
                break;

            case "animstop":
                anim.Stop();
                break;

            case "camdebug":
                Log.Information("[camdebug] {Info}", swap.CameraDebugInfo());
                break;

            case "camoffset":
                if (parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var co))
                {
                    config.CameraHeightOffset = co;
                    PluginInterface.SavePluginConfig(config);
                }
                else
                {
                    Log.Warning("Usage: /mms camoffset <world units, e.g. 2.5>");
                }
                break;

            case "persist":
                config.Persist = parts.Length > 1 ? parts[1] is "on" or "true" or "1" : !config.Persist;
                PluginInterface.SavePluginConfig(config);
                Log.Information("Persist is now {State}", config.Persist ? "on" : "off");
                break;

            default:
                ToggleMainUi();
                break;
        }
    }
}
