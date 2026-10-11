using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_HZP_PlayerModels",
    Name = "Shop PlayerModels",
    Author = "T3Marius & H-AN",
    Version = "1.0.6",
    Description = "ShopCore module with player model items Compatibility with HZP"
)]
public class Shop_PlayerModels : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_HZP_PlayerModels";
    private const string TemplateFileName = "playermodels_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Player Models";
    private const float PreviewDurationSeconds = 8f;
    private const float PreviewDistance = 75f;
    private const float PreviewRotateIntervalSeconds = 0.05f;

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;
    private volatile bool unloaded;
    private int previewGeneration;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, PlayerModelItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly object previewSync = new();
    private readonly Dictionary<int, PlayerModelPreviewState> previewStateByPlayerId = new();
    private long previewSessionCounter;

    private PlayerModelsModuleSettings runtimeSettings = new();

    private readonly Dictionary<string, ShopItemDefinition> itemDefinitionById
    = new(StringComparer.OrdinalIgnoreCase);

    private bool RoundEnd;
    public Shop_PlayerModels(ISwiftlyCore core) : base(core)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        UnregisterItemsAndHandlers();
        shopApi = null;

        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            return;
        }

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogInformation(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. PlayerModels items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        unloaded = false;
        Core.Command.RegisterCommand("shopmodelsreload", ReloadModelConfiguration, true);
        Core.Event.OnPrecacheResource += OnPrecacheResource;
        Core.Event.OnClientDisconnected += OnPreviewClientDisconnected;
        Core.Event.OnClientPutInServer += OnPreviewClientPutInServer;
        Core.Event.OnMapUnload += OnPreviewMapUnload;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }

        if (hotReload)
        {
            Core.Scheduler.NextWorldUpdate(() =>
            {
                if (unloaded) return;
                foreach (var player in Core.PlayerManager.GetAllValidPlayers()) ApplyConfiguredOrDefaultModel(player);
            });
        }
    }

    public override void Unload()
    {
        unloaded = true;
        Core.Event.OnPrecacheResource -= OnPrecacheResource;
        Core.Event.OnClientDisconnected -= OnPreviewClientDisconnected;
        Core.Event.OnClientPutInServer -= OnPreviewClientPutInServer;
        Core.Event.OnMapUnload -= OnPreviewMapUnload;

        ResetModelsOnUnload(Core.PlayerManager.GetAllValidPlayers().Select(player => player.SessionId).ToArray());

        UnregisterItemsAndHandlers();
    }

    private void ReloadModelConfiguration(ICommandContext context)
    {
        if (context.IsSentByPlayer && (context.Sender is not { IsValid: true } player
            || !Core.Permission.PlayerHasPermission(player.SteamID, "shopcore.admin.credits")))
        {
            context.Reply("Permission required: shopcore.admin.credits");
            return;
        }

        if (unloaded || shopApi is null)
        {
            context.Reply("ShopCore API is unavailable.");
            return;
        }

        RegisterItemsAndHandlers();
        context.Reply($"PlayerModels config reloaded. RegisteredItems={registeredItemIds.Count}, PreviewEnabled={runtimeSettings.EnablePreview}. Reopen the shop menu.");
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam e)
    {
        ClearPlayerPreview(e.UserId);
        if (e.OldTeam != 2 || e.Team != 3)
            return HookResult.Continue;

        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        ScheduleConfiguredModel(player, requireActiveRound: true);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        ClearPlayerPreview(e.UserId);
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
        {
            return HookResult.Continue;
        }

        if(pawn.TeamNum == 2)
            return HookResult.Continue;


        ScheduleConfiguredModel(player);

        return HookResult.Continue;
    }

    private void ScheduleConfiguredModel(IPlayer player, bool requireActiveRound = false)
    {
        var connection = player.SessionId;
        var pawnHandle = player.PlayerPawn?.Entity?.EntityHandle.Raw;
        var timer = Core.Scheduler.DelayBySeconds(0.2f, () => Core.Scheduler.NextWorldUpdate(() =>
        {
            var currentPlayer = Core.PlayerManager.GetPlayerFromSessionId(connection);
            if (unloaded || (requireActiveRound && RoundEnd) || currentPlayer?.IsValid != true
                || currentPlayer.IsFakeClient || currentPlayer.PlayerPawn?.Entity?.EntityHandle.Raw != pawnHandle) return;
            ApplyConfiguredOrDefaultModel(currentPlayer);
        }));
        Core.Scheduler.StopOnMapChange(timer);
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart e)
    {
        RoundEnd = false;
        ClearAllPreviewEntities();
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundEnd(EventRoundEnd e)
    {
        RoundEnd = true;
        ClearAllPreviewEntities();
        return HookResult.Continue;
    }

    private void OnPrecacheResource(IOnPrecacheResourceEvent e)
    {
        foreach (var runtime in itemRuntimeById.Values)
        {
            if (string.IsNullOrWhiteSpace(runtime.ModelPath))
            {
                continue;
            }

            e.AddItem(runtime.ModelPath);
        }

        if (!string.IsNullOrWhiteSpace(runtimeSettings.DefaultTModel))
        {
            e.AddItem(runtimeSettings.DefaultTModel);
        }

        if (!string.IsNullOrWhiteSpace(runtimeSettings.DefaultCtModel))
        {
            e.AddItem(runtimeSettings.DefaultCtModel);
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<PlayerModelsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(moduleConfig);

        runtimeSettings = moduleConfig.Settings;

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            var configuredSettings = moduleConfig.Settings;
            moduleConfig = CreateDefaultConfig();
            moduleConfig.Settings = configuredSettings;
            category = moduleConfig.Settings.Category;
            runtimeSettings = moduleConfig.Settings;

            _ = shopApi.SaveModuleConfig(
                ModulePluginId,
                moduleConfig,
                TemplateFileName,
                TemplateSectionName,
                overwrite: true
            );
        }

        var registeredCount = 0;
        foreach (var itemTemplate in moduleConfig.Items)
        {
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register player model item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            itemRuntimeById[definition.Id] = runtime;
            itemDefinitionById[definition.Id] = definition;
            registeredCount++;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_HZP_PlayerModels 1.0.6 initialized. RegisteredItems={RegisteredItems}, PreviewEnabled={PreviewEnabled}",
            registeredCount,
            runtimeSettings.EnablePreview
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        ClearAllPreviewEntities();
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;
        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;
        shopApi.OnItemPreview -= OnItemPreview;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        itemRuntimeById.Clear();
        itemDefinitionById.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!registeredItemIds.Contains(context.Item.Id))
        {
            return;
        }

        if (!itemRuntimeById.TryGetValue(context.Item.Id, out var runtime))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(runtime.RequiredPermission))
        {
            return;
        }

        if (Core.Permission.PlayerHasPermission(context.Player.SteamID, runtime.RequiredPermission))
        {
            return;
        }

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        context.Block($"{GetPrefix(player)} {loc["error.permission", context.Item.DisplayName, runtime.RequiredPermission]}");
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (shopApi is null || !registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (enabled)
        {
            foreach (var otherItemId in registeredItemOrder)
            {
                if (string.Equals(otherItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!shopApi.IsItemEnabled(player, otherItemId))
                {
                    continue;
                }

                _ = shopApi.SetItemEnabled(player, otherItemId, false);
            }
        }

        ApplyConfiguredOrDefaultModel(player);
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal amount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        ApplyConfiguredOrDefaultModel(player);
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        ApplyConfiguredOrDefaultModel(player);
    }

    private void OnItemPreview(IPlayer player, ShopItemDefinition item)
    {
        if (unloaded || !runtimeSettings.EnablePreview || !item.AllowPreview || !registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (!itemRuntimeById.TryGetValue(item.Id, out var runtime))
        {
            return;
        }

        SpawnPreviewModel(player, runtime.ModelPath, item.DisplayName);
    }

    private void ApplyConfiguredOrDefaultModel(IPlayer player)
    {
        if (shopApi is null || player is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
            return;

        if (TryGetEnabledRuntime(player, out var runtime))
        {

            ApplyModel(player, runtime.ModelPath);
            return;
        }

        /*
        if (pawn.TeamNum == 2)
            return;

        ResetToTeamDefaultModel(player);
        */
    }

    private bool TryGetEnabledRuntime(IPlayer player, out PlayerModelItemRuntime runtime)
    {
        runtime = default;

        if (shopApi is null)
        {
            return false;
        }

        foreach (var itemId in registeredItemOrder)
        {
            if (!itemRuntimeById.TryGetValue(itemId, out var itemRuntime))
            {
                continue;
            }

            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            if (!itemDefinitionById.TryGetValue(itemId, out var definition))
            {
                continue;
            }

            if (!IsTeamAllowed(player, definition.Team))
            {
                continue;
            }

            runtime = itemRuntime;
            return true;
        }

        return false;
    }

    private async void ResetModelsOnUnload(ulong[] connections)
    {
        try
        {
            await Core.Scheduler.NextWorldUpdateAsync(() =>
            {
                foreach (var connection in connections)
                {
                    var player = Core.PlayerManager.GetPlayerFromSessionId(connection);
                    if (player is null || !TryGetAlivePawn(player, out var pawn)) continue;
                    var modelPath = ResolveTeamDefaultModel(player);
                    if (!string.IsNullOrWhiteSpace(modelPath)) pawn.SetModel(modelPath);
                }
            });
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to reset player models on unload.");
        }
    }

    private string ResolveTeamDefaultModel(IPlayer player)
    {
        var teamNum = player.Controller.TeamNum;
        if (teamNum == (int)Team.T)
        {
            return runtimeSettings.DefaultTModel;
        }

        if (teamNum == (int)Team.CT)
        {
            return runtimeSettings.DefaultCtModel;
        }

        return string.Empty;
    }

    private void ApplyModel(IPlayer player, string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        var connection = player.SessionId;
        var pawnHandle = player.PlayerPawn?.Entity?.EntityHandle.Raw;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            var currentPlayer = Core.PlayerManager.GetPlayerFromSessionId(connection);
            if (unloaded || currentPlayer?.IsValid != true
                || currentPlayer.PlayerPawn?.Entity?.EntityHandle.Raw != pawnHandle || !TryGetAlivePawn(currentPlayer, out var pawn)) return;

            try
            {
                pawn.SetModel(modelPath);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to apply model '{ModelPath}' to player {PlayerId}.", modelPath, currentPlayer.PlayerID);
            }
        });
    }

    private void SpawnPreviewModel(IPlayer player, string modelPath, string displayName)
    {
        if (unloaded || !runtimeSettings.EnablePreview || string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        modelPath = modelPath.Trim().Replace('\\', '/');
        if (modelPath.EndsWith("_c", StringComparison.Ordinal)) modelPath = modelPath[..^2];
        if (!player.IsValid || player.IsFakeClient) return;
        var connection = player.SessionId;
        var pawnHandle = player.PlayerPawn?.Entity?.EntityHandle.Raw;
        var generation = previewGeneration;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            var currentPlayer = Core.PlayerManager.GetPlayerFromSessionId(connection);
            if (unloaded || !runtimeSettings.EnablePreview || generation != previewGeneration
                || currentPlayer is null || !currentPlayer.IsValid
                || currentPlayer.PlayerPawn?.Entity?.EntityHandle.Raw != pawnHandle || !TryGetAlivePawn(currentPlayer, out var pawn))
            {
                return;
            }

            if (!Core.GameFileSystem.FileExists(modelPath + "_c", "GAME"))
            {
                Core.Logger.LogWarning("Preview model resource '{ModelPath}' is unavailable; no entity will be spawned.", modelPath);
                return;
            }

            var playerId = currentPlayer.PlayerID;
            var state = ReservePreviewSession(playerId, out var previousState);
            if (previousState is not null) DespawnPreviewEntity(previousState);

            CDynamicProp? preview = null;
            var previewHandle = CHandle<CDynamicProp>.Invalid;
            try
            {
                var origin = pawn.AbsOrigin ?? Vector.Zero;
                var rotation = pawn.EyeAngles;
                var yawRadians = rotation.Y * (MathF.PI / 180f);
                var previewPosition = new Vector(
                    origin.X + (MathF.Cos(yawRadians) * PreviewDistance),
                    origin.Y + (MathF.Sin(yawRadians) * PreviewDistance),
                    origin.Z
                );
                var initialYaw = NormalizeYaw(rotation.Y + 180f);
                var previewRotation = new QAngle(0, initialYaw, 0);

                preview = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic_override");
                if (preview is null || !preview.IsValid)
                {
                    ClearPreviewSessionIfCurrent(playerId, state.SessionId);
                    return;
                }

                // 在生成时设置模型和无碰撞标志，避免未生成实体的 SetModel 及物理速度写入。
                using var keyValues = new CEntityKeyValues();
                keyValues.SetString("model", modelPath);
                keyValues.SetVector("origin", previewPosition);
                keyValues.SetQAngle("angles", previewRotation);
                keyValues.SetInt32("solid", 0);
                keyValues.SetInt32("spawnflags", 256);
                preview.Collision.SolidType = SolidType_t.SOLID_NONE;
                preview.Collision.SolidFlags |= 4; // FSOLID_NOT_SOLID
                state.EntityHandle = Core.EntitySystem.GetRefEHandle(preview);
                preview.DispatchSpawn(keyValues);
                if (!preview.IsValid)
                {
                    ClearPreviewSessionIfCurrent(playerId, state.SessionId);
                    return;
                }
                preview.Teleport(previewPosition, previewRotation, null);
                ConfigurePreviewVisibility(preview, currentPlayer);
                previewHandle = Core.EntitySystem.GetRefEHandle(preview);
                state.EntityHandle = previewHandle;

                if (!IsCurrentPreviewSession(playerId, state.SessionId))
                {
                    DespawnPreviewEntity(state);
                    return;
                }

                if (runtimeSettings.RotatePreviewModel)
                {
                    state.RotationTimer = RotatePreviewModel(playerId, state.SessionId, previewHandle, previewRotation);
                }

                currentPlayer.SendChat($"{GetPrefix(currentPlayer)} {Core.Translation.GetPlayerLocalizer(currentPlayer)["preview.started", displayName, (int)PreviewDurationSeconds]}");
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to spawn player model preview for '{DisplayName}'.", displayName);
                if (preview?.IsValid == true)
                {
                    try { preview.Despawn(); }
                    catch (Exception cleanupError) { Core.Logger.LogWarning(cleanupError, "Failed to clean up incomplete preview."); }
                }
                ClearPreviewSessionIfCurrent(playerId, state.SessionId);
                return;
            }

            state.ExpiryTimer = Core.Scheduler.DelayBySeconds(PreviewDurationSeconds, () =>
            {
                Core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!IsCurrentPreviewSession(playerId, state.SessionId))
                    {
                        return;
                    }

                    try
                    {
                        var currentPreview = previewHandle.Value;
                        if (currentPreview?.IsValid == true)
                        {
                            currentPreview.Despawn();
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.LogWarning(ex, "Failed to despawn player model preview.");
                    }
                    finally
                    {
                        ClearPreviewSessionIfCurrent(playerId, state.SessionId);
                    }
                });
            });
            Core.Scheduler.StopOnMapChange(state.ExpiryTimer);
            if (!IsCurrentPreviewSession(playerId, state.SessionId)) CancelPreviewTimers(state);
        });
    }

    private PlayerModelPreviewState ReservePreviewSession(int playerId, out PlayerModelPreviewState? previousState)
    {
        lock (previewSync)
        {
            previewStateByPlayerId.TryGetValue(playerId, out previousState);

            previewSessionCounter++;
            var state = new PlayerModelPreviewState(previewSessionCounter);
            previewStateByPlayerId[playerId] = state;
            return state;
        }
    }

    private bool IsCurrentPreviewSession(int playerId, long sessionId)
    {
        lock (previewSync)
        {
            return previewStateByPlayerId.TryGetValue(playerId, out var state) && state.SessionId == sessionId;
        }
    }

    private void ClearPreviewSessionIfCurrent(int playerId, long sessionId)
    {
        PlayerModelPreviewState? removedState = null;
        lock (previewSync)
        {
            if (previewStateByPlayerId.TryGetValue(playerId, out var state) && state.SessionId == sessionId)
            {
                previewStateByPlayerId.Remove(playerId);
                removedState = state;
            }
        }
        if (removedState is not null) CancelPreviewTimers(removedState);
    }

    private static void CancelPreviewTimers(PlayerModelPreviewState state)
    {
        // StopOnMapChange 仍持有令牌回调，由调度器管理其生命周期，此处只取消。
        state.RotationTimer?.Cancel();
        state.ExpiryTimer?.Cancel();
    }

    private void DespawnPreviewEntity(PlayerModelPreviewState state)
    {
        CancelPreviewTimers(state);
        try
        {
            // 句柄同时校验实体索引和序列号，不能误删复用索引后的其他实体。
            var existing = state.EntityHandle.Value;
            if (existing?.IsValid == true)
            {
                existing.Despawn();
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to despawn existing preview entity {EntityHandle}.", state.EntityHandle.Raw);
        }
    }

    private void ClearAllPreviewEntities()
    {
        PlayerModelPreviewState[] entityStates;
        lock (previewSync)
        {
            previewGeneration++;
            entityStates = previewStateByPlayerId.Values.ToArray();
            previewStateByPlayerId.Clear();
        }

        foreach (var state in entityStates) CancelPreviewTimers(state);
        if (entityStates.Length > 0) CleanupPreviewEntities(entityStates);
    }

    private async void CleanupPreviewEntities(PlayerModelPreviewState[] states)
    {
        try
        {
            // 卸载会取消插件范围的普通回调，使用全局主线程任务完成最后一次清理。
            await Core.Scheduler.NextWorldUpdateAsync(() =>
            {
                foreach (var state in states) DespawnPreviewEntity(state);
            });
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to schedule preview cleanup.");
        }
    }

    private string GetPrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (runtimeSettings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        return loc["shop.prefix"];
    }

    private void ConfigurePreviewVisibility(CDynamicProp preview, IPlayer previewOwner)
    {
        if (preview is null || !preview.IsValid || previewOwner is null || !previewOwner.IsValid)
        {
            return;
        }

        if (previewOwner.PlayerID < 0)
        {
            return;
        }

        try
        {
            // 隐藏所有槽位的预览，仅向发起预览的玩家发送。
            preview.SetTransmitState(false);
            preview.SetTransmitState(true, previewOwner.PlayerID);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to configure preview visibility for player {PlayerId}.", previewOwner.PlayerID);
        }
    }

    private CancellationTokenSource RotatePreviewModel(int playerId, long previewSession, CHandle<CDynamicProp> previewHandle, QAngle initialRotation)
    {
        var steps = (int)MathF.Ceiling(PreviewDurationSeconds / PreviewRotateIntervalSeconds);
        var rotationTimer = Core.Scheduler.AddTimer(timer =>
        {
            if (unloaded || !IsCurrentPreviewSession(playerId, previewSession) || timer.ExecutionCount >= (ulong)steps)
                return SwiftlyS2.Shared.Scheduler.TimerStep.Stop();

            var targetYaw = NormalizeYaw(initialRotation.Y + (360f * timer.ExecutionCount / steps));
            Core.Scheduler.NextWorldUpdate(() =>
            {
                if (unloaded || !IsCurrentPreviewSession(playerId, previewSession)) return;
                var preview = previewHandle.Value;
                if (preview?.IsValid != true) return;
                try { preview.Teleport(null, new QAngle(0, targetYaw, 0), null); }
                catch (Exception ex) { Core.Logger.LogWarning(ex, "Failed rotating preview player model entity."); }
            });
            return SwiftlyS2.Shared.Scheduler.TimerStep.WaitForSeconds(PreviewRotateIntervalSeconds);
        });
        Core.Scheduler.StopOnMapChange(rotationTimer);
        return rotationTimer;
    }

    private void ClearPlayerPreview(int playerId)
    {
        PlayerModelPreviewState? state;
        lock (previewSync)
        {
            if (!previewStateByPlayerId.Remove(playerId, out state)) return;
        }
        CancelPreviewTimers(state);
        CleanupPreviewEntities([state]);
    }

    private void OnPreviewClientPutInServer(IOnClientPutInServerEvent e)
    {
        ClearPlayerPreview(e.PlayerId);
        var connection = Core.PlayerManager.GetPlayer(e.PlayerId)?.SessionId;
        if (connection is null) return;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            var player = Core.PlayerManager.GetPlayerFromSessionId(connection.Value);
            if (unloaded || player?.IsValid != true) return;
            lock (previewSync)
            {
                foreach (var (ownerId, state) in previewStateByPlayerId)
                {
                    if (ownerId == player.PlayerID) continue;
                    var preview = state.EntityHandle.Value;
                    if (preview?.IsValid == true) preview.SetTransmitState(false, player.PlayerID);
                }
            }
        });
    }

    private void OnPreviewClientDisconnected(IOnClientDisconnectedEvent e) => ClearPlayerPreview(e.PlayerId);
    private void OnPreviewMapUnload(IOnMapUnloadEvent e) => ClearAllPreviewEntities();

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPreviewPlayerDeath(EventPlayerDeath e)
    {
        ClearPlayerPreview(e.UserId);
        return HookResult.Continue;
    }

    private static float NormalizeYaw(float yaw)
    {
        var normalized = yaw % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private static bool TryGetAlivePawn(IPlayer player, out CCSPlayerPawn pawn)
    {
        pawn = null!;

        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        var playerPawn = player.PlayerPawn;
        if (playerPawn is null || !playerPawn.IsValid || playerPawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
        {
            return false;
        }

        pawn = playerPawn;
        return true;
    }

    private bool TryCreateDefinition(
        PlayerModelItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out PlayerModelItemRuntime runtime)
    {
        definition = default!;
        runtime = default;

        if (string.IsNullOrWhiteSpace(itemTemplate.Id))
        {
            return false;
        }

        var itemId = itemTemplate.Id.Trim();
        if (itemTemplate.Price <= 0)
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Price must be greater than 0.", itemId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(itemTemplate.ModelPath))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because ModelPath is empty.", itemId);
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Type, ignoreCase: true, out ShopItemType itemType))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Type '{Type}' is invalid.", itemId, itemTemplate.Type);
            return false;
        }

        if (itemType == ShopItemType.Consumable)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because player model items cannot use Type '{Type}'.",
                itemId,
                itemType
            );
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team))
        {
            team = ShopItemTeam.Any;
        }

        TimeSpan? duration = null;
        if (itemTemplate.DurationSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(itemTemplate.DurationSeconds);
        }

        if (itemType == ShopItemType.Temporary && !duration.HasValue)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because Temporary items require DurationSeconds > 0.",
                itemId
            );
            return false;
        }

        decimal? sellPrice = null;
        if (itemTemplate.SellPrice.HasValue && itemTemplate.SellPrice.Value >= 0)
        {
            sellPrice = itemTemplate.SellPrice.Value;
        }

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: ResolveDisplayName(itemTemplate),
            Category: category,
            Price: itemTemplate.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: itemTemplate.Enabled,
            CanBeSold: itemTemplate.CanBeSold,
            AllowPreview: runtimeSettings.EnablePreview
        );

        runtime = new PlayerModelItemRuntime(
            ItemId: itemId,
            ModelPath: itemTemplate.ModelPath.Trim(),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(PlayerModelItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? Core.Localizer[key, itemTemplate.ModelName]
                : Core.Localizer[key, itemTemplate.ModelName, FormatDuration(itemTemplate.DurationSeconds)];

            if (!string.Equals(localized, key, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName))
        {
            return itemTemplate.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.ModelName))
        {
            return itemTemplate.ModelName.Trim();
        }

        return itemTemplate.Id.Trim();
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0 Seconds";
        }

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
        {
            var hours = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            return minutes > 0
                ? $"{hours} Hour{(hours == 1 ? "" : "s")} {minutes} Minute{(minutes == 1 ? "" : "s")}" 
                : $"{hours} Hour{(hours == 1 ? "" : "s")}";
        }

        if (ts.TotalMinutes >= 1)
        {
            var minutes = (int)ts.TotalMinutes;
            var seconds = ts.Seconds;
            return seconds > 0
                ? $"{minutes} Minute{(minutes == 1 ? "" : "s")} {seconds} Second{(seconds == 1 ? "" : "s")}" 
                : $"{minutes} Minute{(minutes == 1 ? "" : "s")}";
        }

        return $"{ts.Seconds} Second{(ts.Seconds == 1 ? "" : "s")}";
    }

    private static void NormalizeConfig(PlayerModelsModuleConfig config)
    {
        config.Settings ??= new PlayerModelsModuleSettings();
        config.Items ??= [];

        config.Settings.Category = string.IsNullOrWhiteSpace(config.Settings.Category)
            ? DefaultCategory
            : config.Settings.Category.Trim();

        config.Settings.DefaultTModel = string.IsNullOrWhiteSpace(config.Settings.DefaultTModel)
            ? "characters/models/tm_phoenix/tm_phoenix.vmdl"
            : config.Settings.DefaultTModel.Trim();

        config.Settings.DefaultCtModel = string.IsNullOrWhiteSpace(config.Settings.DefaultCtModel)
            ? "characters/models/ctm_sas/ctm_sas.vmdl"
            : config.Settings.DefaultCtModel.Trim();
    }

    private static PlayerModelsModuleConfig CreateDefaultConfig()
    {
        return new PlayerModelsModuleConfig
        {
            Settings = new PlayerModelsModuleSettings
            {
                Category = DefaultCategory,
                DefaultTModel = "characters/models/tm_phoenix/tm_phoenix.vmdl",
                DefaultCtModel = "characters/models/ctm_sas/ctm_sas.vmdl",
                RotatePreviewModel = true
            },
            Items =
            [
                new PlayerModelItemTemplate
                {
                    Id = "model_frogman_hourly",
                    ModelName = "Frogman",
                    DisplayNameKey = "item.temporary.name",
                    ModelPath = "characters/models/ctm_diver/ctm_diver_variantb.vmdl",
                    Price = 3500,
                    SellPrice = 1750,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new PlayerModelItemTemplate
                {
                    Id = "model_fbi_permanent",
                    ModelName = "FBI",
                    DisplayNameKey = "item.permanent.name",
                    ModelPath = "characters/models/ctm_fbi/ctm_fbi_varianta.vmdl",
                    Price = 9000,
                    SellPrice = 4500,
                    DurationSeconds = 0,
                    Type = nameof(ShopItemType.Permanent),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }

    private bool IsTeamAllowed(IPlayer player, ShopItemTeam team)
    {
        if (team == ShopItemTeam.Any)
            return true;

        var pawn = player.PlayerPawn;
        if(pawn == null || !pawn.IsValid)
            return false;

        var teamNum = pawn.TeamNum;

        if (team == ShopItemTeam.T && teamNum == 2)
            return true;

        if (team == ShopItemTeam.CT && teamNum == 3)
            return true;

        return false;
    }
}

internal readonly record struct PlayerModelItemRuntime(
    string ItemId,
    string ModelPath,
    string RequiredPermission
);

internal sealed class PlayerModelPreviewState(long sessionId)
{
    public long SessionId { get; } = sessionId;
    public CHandle<CDynamicProp> EntityHandle { get; set; } = CHandle<CDynamicProp>.Invalid;
    public CancellationTokenSource? RotationTimer { get; set; }
    public CancellationTokenSource? ExpiryTimer { get; set; }
}

internal sealed class PlayerModelsModuleConfig
{
    public PlayerModelsModuleSettings Settings { get; set; } = new();
    public List<PlayerModelItemTemplate> Items { get; set; } = [];
}

internal sealed class PlayerModelsModuleSettings
{
    public bool UseCorePrefix { get; set; } = true;
    public string Category { get; set; } = "Visuals/Player Models";
    public string DefaultTModel { get; set; } = "characters/models/tm_phoenix/tm_phoenix.vmdl";
    public string DefaultCtModel { get; set; } = "characters/models/ctm_sas/ctm_sas.vmdl";
    public bool EnablePreview { get; set; } = true;
    public bool RotatePreviewModel { get; set; } = true;
}

internal sealed class PlayerModelItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public string RequiredPermission { get; set; } = string.Empty;
}
