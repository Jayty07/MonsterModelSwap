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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("MonsterModelSwap");
    private readonly Configuration config;
    private readonly ModelDatabase database;
    private readonly ModelSwapService swap;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        database = new ModelDatabase(PluginInterface, DataManager, Log);
        swap = new ModelSwapService(Framework, ClientState, Condition, ObjectTable, Log, config, new CameraScaleService(Log, ObjectTable, GameInterop));
        mainWindow = new MainWindow(PluginInterface, config, database, swap);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Monster Model Swap window. /mms apply <id> | revert | persist [on|off] | height <x> | camoffset <y> | camdebug",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        if (config.OpenOnStartup) mainWindow.IsOpen = true;
        if (config.ApplyOnLogin && config.SelectedModelId > 0 && ClientState.IsLoggedIn)
            swap.Apply(config.SelectedModelId, config.Height);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        CommandManager.RemoveHandler(Command);
        windowSystem.RemoveAllWindows();

        if (swap.Active) swap.Revert();
        swap.Dispose();
    }

    private void ToggleMainUi() => mainWindow.Toggle();

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
