using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlareCounter;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/glare";

    // Glare, Glare III, Glare IV
    private static readonly HashSet<uint> GlareIds = new() { 16533, 25859, 37009 };

    // Swiftcast status ID
    private const uint SwiftcastStatusId = 167;

    private delegate bool UseActionDelegate(
        ActionManager* self, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInterop { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private readonly Configuration config;
    private readonly WindowSystem windows = new("GlareCounter");
    private readonly MainWindow window;

    public int PullCount { get; private set; }
    public int DutyCount { get; private set; }
    public long LifetimeCount => config.LifetimeTotal;

    private bool wasInCombat;
    private uint pendingId;
    private float pendingProgress;
    private float pendingTotal;

    private readonly Hook<UseActionDelegate> useActionHook;
    private long lastInstantTick;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        window = new MainWindow(this) { IsOpen = config.ShowWindow };
        windows.AddWindow(window);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "/glare: toggle window | /glare reset: reset pull and duty counts"
        });

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        Framework.Update += OnUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        useActionHook = GameInterop.HookFromAddress<UseActionDelegate>(
            (nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour);
        useActionHook.Enable();
    }

    public void Dispose()
    {
        useActionHook.Dispose();
        Framework.Update -= OnUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        CommandManager.RemoveHandler(Command);

        config.ShowWindow = window.IsOpen;
        PluginInterface.SavePluginConfig(config);
        windows.RemoveAllWindows();
    }

    public void ResetCurrent()
    {
        PullCount = 0;
        DutyCount = 0;
    }

    public void ResetLifetime()
    {
        config.LifetimeTotal = 0;
        PluginInterface.SavePluginConfig(config);
    }

    private void ToggleWindow() => window.IsOpen = !window.IsOpen;

    private void OnCommand(string command, string args)
    {
        if (args.Trim().ToLowerInvariant() == "reset")
            ResetCurrent();
        else
            ToggleWindow();
    }

    private void OnTerritoryChanged(uint territory)
    {
        PullCount = 0;
        DutyCount = 0;
        pendingId = 0;
    }

    private void Register()
    {
        PullCount++;
        DutyCount++;
        config.LifetimeTotal++;
        if (config.LifetimeTotal % 25 == 0)
            PluginInterface.SavePluginConfig(config);
    }

    private bool HasSwiftcast()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null) return false;
        foreach (var status in player.StatusList)
            if (status.StatusId == SwiftcastStatusId)
                return true;
        return false;
    }

    // Hard-cast Glares are counted by watching the cast bar (see OnUpdate).
    // Instant Glares (Swiftcast) never show a cast bar, so catch them here.
    private bool UseActionDetour(
        ActionManager* self, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        // The hotbar can pass the base action (e.g. Stone) instead of Glare, so check both.
        var isGlare = false;
        if (actionType == ActionType.Action)
        {
            var adjusted = self->GetAdjustedActionId(actionId);
            isGlare = GlareIds.Contains(actionId) || GlareIds.Contains(adjusted);
        }

        // Must be checked BEFORE calling the original, since Swiftcast is consumed by the cast.
        var swift = isGlare && HasSwiftcast();

        var result = useActionHook.Original(self, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        if (isGlare)
            Log.Information($"[GlareCounter] UseAction glare id={actionId} swift={swift} result={result} mode={mode}");

        if (result && swift)
        {
            // Debounce: ignore repeat calls within 600 ms (queued/spammed presses).
            var now = System.Environment.TickCount64;
            if (now - lastInstantTick > 600)
            {
                lastInstantTick = now;
                Register();
            }
        }
        return result;
    }

    private void OnUpdate(IFramework framework)
    {
        // New pull = entering combat.
        var inCombat = Condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat)
            PullCount = 0;
        wasInCombat = inCombat;

        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            pendingId = 0;
            return;
        }

        // CastActionType 1 == Action
        if (player.IsCasting && (uint)player.CastActionType == 1 && GlareIds.Contains(player.CastActionId))
        {
            pendingId = player.CastActionId;
            pendingProgress = player.CurrentCastTime;
            pendingTotal = player.TotalCastTime;
            return;
        }

        // The cast just stopped. Count it only if it ran (almost) to completion,
        // so cancelled/interrupted casts don't count.
        if (pendingId != 0)
        {
            if (pendingTotal > 0 && pendingProgress >= pendingTotal - 0.2f)
            {
                Register();
            }
            pendingId = 0;
        }
    }
}

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("Glare Counter###GlareCounterMain")
    {
        this.plugin = plugin;
        Size = new Vector2(200, 130);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"This pull:  {plugin.PullCount}");
        ImGui.TextUnformatted($"This duty:  {plugin.DutyCount}");
        ImGui.TextUnformatted($"Lifetime:   {plugin.LifetimeCount}");
        ImGui.Spacing();
        if (ImGui.Button("Reset pull/duty"))
            plugin.ResetCurrent();
        ImGui.SameLine();
        if (ImGui.Button("Reset all"))
        {
            plugin.ResetCurrent();
            plugin.ResetLifetime();
        }
    }
}
