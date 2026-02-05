using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Events;
using RetakesAllocatorCore.Managers;
using RetakesAllocator.Menus;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using RetakesAllocator.AdvancedMenus;
using static RetakesAllocatorCore.PluginInfo;
using RetakesPluginShared;
using RetakesPluginShared.Events;
using KitsuneMenu.Core;

namespace RetakesAllocator;

[MinimumApiVersion(201)]
public class RetakesAllocator : BasePlugin
{
    public override string ModuleName => "Retakes Allocator Plugin";
    public override string ModuleVersion => PluginInfo.Version;
    public override string ModuleAuthor => "Yoni Lerner, B3none, Gold KingZ";
    public override string ModuleDescription => "https://github.com/yonilerner/cs2-retakes-allocator";

    private readonly AllocatorMenuManager _allocatorMenuManager = new();
    private readonly AdvancedGunMenu _advancedGunMenu = new();
    private readonly Dictionary<CCSPlayerController, Dictionary<ItemSlotType, CsItem>> _allocatedPlayerItems = new();
    private readonly Dictionary<ulong, DateTime> _gunCommandCooldowns = new();
    private const double GunCommandCooldownSeconds = 5.0;
    private IRetakesPluginEventSender? RetakesPluginEventSender { get; set; }

    private CustomGameData? CustomFunctions { get; set; }

    private bool IsAllocatingForRound { get; set; }
    private string _bombsite = "";
    private bool _announceBombsite;
    private bool _bombsiteAnnounceOneTime;
    // Cached values for OnTick optimization - computed once when announcement starts
    private int _cachedCtCount;
    private int _cachedTCount;
    private string _cachedBombsiteImage = "";
    private bool _weaponDataSignatureFailed;

    #region Setup

    public override void Load(bool hotReload)
    {
        Configs.Shared.Module = ModuleDirectory;

        Log.Debug($"Loaded. Hot reload: {hotReload}");
        ResetState();
        KitsuneMenu.KitsuneMenu.Init();

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            // Flush cache before map change
            _ = PlayerSettingsCache.ClearAsync();
            ResetState();
            Log.Debug($"Setting map name {mapName}");
            RoundTypeManager.Instance.SetMap(mapName);
        });

        // Register player connect for cache pre-loading
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        var useCustomGameData =
            Configs.GetConfigData().EnableCanAcquireHook || Configs.GetConfigData().CapabilityWeaponPaints;

        if (useCustomGameData)
        {
            _ = Task.Run(async () =>
            {
                var downloadedNewGameData = await Helpers.DownloadMissingFiles();
                if (!downloadedNewGameData)
                {
                    return;
                }

                Server.NextFrame(() =>
                {
                    CustomFunctions ??= new();
                    // Must unhook the old functions before reloading and rehooking
                    CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Unhook(OnWeaponCanAcquire, HookMode.Pre);
                    CustomFunctions.LoadCustomGameData();
                    if (Configs.GetConfigData().EnableCanAcquireHook)
                    {
                        CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Hook(OnWeaponCanAcquire, HookMode.Pre);
                    }
                });
            });
        }

        if (Configs.GetConfigData().UseOnTickFeatures)
        {
            RegisterListener<Listeners.OnTick>(OnTick);
        }

        AddTimer(0.1f, () => { GetRetakesPluginEventSender().RetakesPluginEventHandlers += RetakesEventHandler; });

        // Periodic cache flush (every 120 seconds)
        AddTimer(120.0f, () => _ = PlayerSettingsCache.FlushDirtyPlayersAsync(), TimerFlags.REPEAT);

        if (Configs.GetConfigData().MigrateOnStartup)
        {
            Queries.Migrate();
        }

        if (useCustomGameData)
        {
            CustomFunctions = new();

            if (Configs.GetConfigData().EnableCanAcquireHook)
            {
                CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Hook(OnWeaponCanAcquire, HookMode.Pre);
            }
        }

        if (hotReload)
        {
            HandleHotReload();
        }
    }

    private void ResetState(bool loadConfig = true)
    {
        if (loadConfig)
        {
            Configs.Load(ModuleDirectory, true);
        }

        Translator.Initialize(Localizer);

        RoundTypeManager.Instance.SetNextRoundTypeOverride(null);
        RoundTypeManager.Instance.SetCurrentRoundType(null);
        RoundTypeManager.Instance.Initialize();

        _allocatedPlayerItems.Clear();
        _bombsite = "";
        _announceBombsite = false;
        _bombsiteAnnounceOneTime = false;
    }

    private void HandleHotReload()
    {
        Server.ExecuteCommand($"map {Server.MapName}");
    }

    public override void Unload(bool hotReload)
    {
        Log.Debug("Unloaded");

        // Flush all cached data before unloading
        PlayerSettingsCache.FlushDirtyPlayersAsync().GetAwaiter().GetResult();

        KitsuneMenu.KitsuneMenu.Cleanup();
        ResetState(loadConfig: false);
        Queries.Disconnect();

        GetRetakesPluginEventSender().RetakesPluginEventHandlers -= RetakesEventHandler;

        if (Configs.GetConfigData().EnableCanAcquireHook && CustomFunctions != null)
        {
            CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Unhook(OnWeaponCanAcquire, HookMode.Pre);
        }

        if (CustomFunctions != null)
        {
            // Clear references to custom game data to avoid native calls after unload
            CustomFunctions = null;
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid != true || player.IsBot)
        {
            return HookResult.Continue;
        }

        var steamId = Helpers.GetSteamId(player);
        // Pre-load player settings into cache (async, non-blocking)
        _ = PlayerSettingsCache.LoadPlayerAsync(steamId);

        return HookResult.Continue;
    }

    private IRetakesPluginEventSender GetRetakesPluginEventSender()
    {
        if (RetakesPluginEventSender is not null)
        {
            return RetakesPluginEventSender;
        }

        var sender = new PluginCapability<IRetakesPluginEventSender>("retakes_plugin:event_sender").Get();
        if (sender is null)
        {
            throw new Exception("Couldn't load retakes plugin event sender capability");
        }

        RetakesPluginEventSender = sender;
        return sender;
    }

    private void RetakesEventHandler(object? _, IRetakesPluginEvent @event)
    {
        Log.Trace("Got retakes event");
        Action? handler = @event switch
        {
            AllocateEvent => HandleAllocateEvent,
            _ => null
        };
        handler?.Invoke();
    }

    #endregion

    #region Commands

    [ConsoleCommand("css_nextround", "Opens the menu to vote for the next round type.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnNextRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}This command can only be executed by a valid player.");
            return;
        }

        if (!Configs.GetConfigData().EnableNextRoundTypeVoting)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Next round voting is disabled.");
            return;
        }

        _allocatorMenuManager.OpenMenuForPlayer(player!, MenuType.NextRoundVote);
    }

    [ConsoleCommand("css_gun")]
    [CommandHelper(usage: "<gun> [T|CT]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Configs.GetConfigData().GunCommandsEnabled)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Gun command is currently disabled by server config.");
            return;
        }
        HandleWeaponCommand(player, commandInfo);
    }

    private void HandleWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        var currentTeam = player!.Team;

        // Check cooldown for weapon swap (not for preference save)
        if (player.PawnIsAlive && _gunCommandCooldowns.TryGetValue(playerId, out var lastUsed))
        {
            var elapsed = (DateTime.UtcNow - lastUsed).TotalSeconds;
            if (elapsed < GunCommandCooldownSeconds)
            {
                var remaining = Math.Ceiling(GunCommandCooldownSeconds - elapsed);
                commandInfo.ReplyToCommand($"{PluginInfo.MessagePrefix}Please wait {remaining}s before changing weapon again.");
                return;
            }
        }

        var result = OnWeaponCommandHelper.HandleFromCache(
            Helpers.CommandInfoToArgList(commandInfo),
            playerId,
            RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam,
            false,
            out var selectedWeapon
        );
        Helpers.WriteNewlineDelimited(result, commandInfo.ReplyToCommand);

        // Immediately swap weapon if valid for current round and player is alive
        if (selectedWeapon.HasValue && player.PawnIsAlive)
        {
            var selectedWeaponAllocationType =
                WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(RoundTypeManager.Instance.GetCurrentRoundType(),
                    currentTeam,
                    selectedWeapon.Value);
            if (selectedWeaponAllocationType is not null)
            {
                // Update cooldown
                _gunCommandCooldowns[playerId] = DateTime.UtcNow;

                // Remove weapon in same slot BEFORE allocating new one (like original source)
                Helpers.RemoveWeapons(
                    player,
                    item =>
                        WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                            RoundTypeManager.Instance.GetCurrentRoundType(), currentTeam, item) ==
                        selectedWeaponAllocationType
                );

                var slotType = WeaponHelpers.GetSlotTypeForItem(selectedWeapon.Value);
                var slotName = WeaponHelpers.GetSlotNameForSlotType(slotType);
                AllocateItemsForPlayer(player, new List<CsItem> { selectedWeapon.Value }, slotName);
            }
        }
    }

    [ConsoleCommand("css_awp", "Join or leave the AWP queue.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAwpCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        var currentTeam = player!.Team;

        var awpMode = Configs.GetConfigData().GetAwpMode();
        if (awpMode == AccessMode.Disabled)
        {
            var message = Translator.Instance["weapon_preference.awp_disabled"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        if (awpMode == AccessMode.VipOnly && !Helpers.HasAwpPermission(player))
        {
            var message = Translator.Instance["weapon_preference.only_vip_can_use"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        // Read from cache - instant, no blocking
        var cachedSettings = PlayerSettingsCache.GetSettings(playerId);
        var currentPreferredSetting = cachedSettings?.GetWeaponPreference(currentTeam, WeaponAllocationType.Preferred);
        var removing = currentPreferredSetting is not null;

        // Determine new preference
        CsItem? newPreference = removing ? null : CsItem.AWP;

        // Update cache immediately (batched DB write later)
        PlayerSettingsCache.SetPreferredWeapon(playerId, newPreference);

        // Generate response message
        var messageKey = removing
            ? "sniper_weapon.stopped"
            : "sniper_weapon.started";
        var responseMsg = Translator.Instance[messageKey, CsItem.AWP.ToString()];
        commandInfo.ReplyToCommand($"{MessagePrefix}{responseMsg}");
    }

    [ConsoleCommand("css_ssg", "Join or leave the SSG queue.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnSsgCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var ssgMode = Configs.GetConfigData().GetSsgMode();
        if (ssgMode == AccessMode.Disabled)
        {
            var message = Translator.Instance["weapon_preference.ssg_disabled"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        if (ssgMode == AccessMode.VipOnly && !Helpers.HasSsgPermission(player!))
        {
            var message = Translator.Instance["weapon_preference.only_vip_can_use"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        var currentTeam = player!.Team;

        // Read from cache - instant, no blocking
        var cachedSettings = PlayerSettingsCache.GetSettings(playerId);
        var currentPreferredSetting = cachedSettings?.GetWeaponPreference(currentTeam, WeaponAllocationType.Preferred);
        var removing = currentPreferredSetting == CsItem.Scout;

        // Determine new preference
        CsItem? newPreference = removing ? null : CsItem.Scout;

        // Update cache immediately (batched DB write later)
        PlayerSettingsCache.SetPreferredWeapon(playerId, newPreference);

        // Generate response message
        var messageKey = removing
            ? "sniper_weapon.stopped"
            : "sniper_weapon.started";
        var responseMsg = Translator.Instance[messageKey, CsItem.Scout.ToString()];
        commandInfo.ReplyToCommand($"{MessagePrefix}{responseMsg}");
    }

    [ConsoleCommand("css_zeus", "Toggle whether you will receive a Zeus.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnZeusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        if (!Configs.GetConfigData().IsZeusEnabled())
        {
            var message = Translator.Instance["guns_menu.zeus_disabled_message"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        // Read from cache - instant, no blocking
        var cachedSettings = PlayerSettingsCache.GetSettings(playerId);
        var currentlyEnabled = cachedSettings?.ZeusEnabled ?? false;
        var toggled = !currentlyEnabled;

        // Update cache immediately (batched DB write later)
        PlayerSettingsCache.SetZeusPreference(playerId, toggled);

        var messageKey = toggled ? "guns_menu.zeus_enabled_message" : "guns_menu.zeus_disabled_message";
        Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], commandInfo.ReplyToCommand);
    }
    [ConsoleCommand("css_removegun")]
    [CommandHelper(minArgs: 1, usage: "<gun> [T|CT]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRemoveWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Configs.GetConfigData().GunCommandsEnabled)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Gun command is currently disabled by server config.");
            return;
        }
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        var currentTeam = player!.Team;

        var result = OnWeaponCommandHelper.HandleFromCache(
            Helpers.CommandInfoToArgList(commandInfo),
            playerId,
            RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam,
            true,
            out _
        );
        commandInfo.ReplyToCommand($"{MessagePrefix}{result}");
    }

    [ConsoleCommand("css_setnextround", "Sets the next round type.")]
    [CommandHelper(minArgs: 1, usage: "<P/H/F>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnSetNextRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        var roundTypeInput = commandInfo.GetArg(1).ToLower();
        var roundType = RoundTypeHelpers.ParseRoundType(roundTypeInput);
        if (roundType is null)
        {
            var message = Translator.Instance["announcement.next_roundtype_set_invalid", roundTypeInput];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
        }
        else
        {
            RoundTypeManager.Instance.SetNextRoundTypeOverride(roundType);
            var roundTypeName = RoundTypeHelpers.TranslateRoundTypeName(roundType.Value);
            var message = Translator.Instance["announcement.next_roundtype_set", roundTypeName];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
        }
    }

    [ConsoleCommand("css_reload_allocator_config", "Reloads the cs2-retakes-allocator config.")]
    [RequiresPermissions("@css/root")]
    public void OnReloadAllocatorConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        commandInfo.ReplyToCommand($"{MessagePrefix}Reloading config for version {ModuleVersion}");
        Configs.Load(ModuleDirectory);
        RoundTypeManager.Instance.Initialize();
    }

    [ConsoleCommand("css_print_config", "Print the entire config or a specific config.")]
    [CommandHelper(usage: "<config>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnPrintConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        var configName = commandInfo.ArgCount > 1 ? commandInfo.GetArg(1) : null;
        var response = Configs.StringifyConfig(configName);
        if (response is null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid config name.");
            return;
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{response}");
        Log.Info(response);
    }

    #endregion

    #region Events

    public HookResult OnWeaponCanAcquire(DynamicHook hook)
    {
        Log.Debug("OnWeaponCanAcquire");

        var acquireMethod = hook.GetParam<AcquireMethod>(2);
        if (acquireMethod == AcquireMethod.PickUp)
        {
            return HookResult.Continue;
        }

        var isWarmup = Helpers.IsWarmup();

        if (isWarmup)
        {
            return HookResult.Continue;
        }

        // Log.Trace($"OnWeaponCanAcquire enter {IsAllocatingForRound}");
        if (IsAllocatingForRound)
        {
            Log.Debug("Skipping OnWeaponCanAcquire because we're allocating for round");
            return HookResult.Continue;
        }

        HookResult RetStop()
        {
            // Log.Debug($"Exiting OnWeaponCanAcquire {acquireMethod}");
            hook.SetReturn(
                acquireMethod != AcquireMethod.PickUp
                    ? AcquireResult.AlreadyOwned
                    : AcquireResult.InvalidItem
            );

            return HookResult.Stop;
        }

        if (CustomFunctions is null)
        {
            return RetStop();
        }

        if (_weaponDataSignatureFailed)
        {
            return HookResult.Continue;
        }

        CCSWeaponBaseVData? weaponData = null;
        try
        {
            weaponData = CustomFunctions.GetCSWeaponDataFromKeyFunc?.Invoke(-1,
                hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString());
        }
        catch (NativeException ex)
        {
            _weaponDataSignatureFailed = true;
            CustomFunctions.GetCSWeaponDataFromKeyFunc = null;
            Log.Error(
                $"GetCSWeaponDataFromKey invocation failed. This usually means your RetakesAllocator_gamedata.json signatures are outdated. Error: {ex.Message}");
            return HookResult.Continue;
        }

        var player = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value.Controller.Value?.As<CCSPlayerController>();
        if (player is null || !player.IsValid || !player.PawnIsAlive)
        {
            Log.Debug($"Invalid player controller {player} {player?.IsValid} {player?.PawnIsAlive}");
            return HookResult.Continue;
        }

        if (weaponData == null)
        {
            Log.Warn($"Invalid weapon data {hook.GetParam<CEconItemView>(1).ItemDefinitionIndex}");
            return HookResult.Continue;
        }

        var team = player.Team;
        var item = Utils.ToEnum<CsItem>(weaponData.Name);

        if (item is CsItem.KnifeT or CsItem.KnifeCT)
        {
            return HookResult.Continue;
        }

        if (item is CsItem.Taser)
        {
            var config = Configs.GetConfigData();
            if (!config.IsZeusEnabled())
            {
                return RetStop();
            }

            var steamId = Helpers.GetSteamId(player);
            if (steamId == 0)
            {
                return RetStop();
            }

            // Check cache first (contains latest settings), fallback to database
            var cachedSetting = PlayerSettingsCache.GetSettings(steamId);
            if (cachedSetting != null)
            {
                return cachedSetting.ZeusEnabled == true ? HookResult.Continue : RetStop();
            }

            // Fallback to database if not in cache
            var userSettings = Queries.GetUsersSettings(new[] { steamId });
            userSettings.TryGetValue(steamId, out var userSetting);

            return userSetting?.ZeusEnabled == true ? HookResult.Continue : RetStop();
        }
        if (!WeaponHelpers.IsUsableWeapon(item))
        {
            return RetStop();
        }

        var isPreferred = WeaponHelpers.IsPreferred(team, item);
        var purchasedAllocationType = RoundTypeManager.Instance.GetCurrentRoundType() is not null
            ? WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                RoundTypeManager.Instance.GetCurrentRoundType(), team, item
            )
            : null;
        var isValidAllocation = WeaponHelpers.IsAllocationTypeValidForRound(purchasedAllocationType,
            RoundTypeManager.Instance.GetCurrentRoundType());

        // Log.Debug($"item {item} team {team} player {playerId}");
        // Log.Debug($"weapon alloc {purchasedAllocationType} valid? {isValidAllocation}");
        // Log.Debug($"Preferred? {isPreferred}");

        if (
            Helpers.IsWeaponAllocationAllowed() &&
            !isPreferred &&
            isValidAllocation &&
            purchasedAllocationType is not null
        )
        {
            return HookResult.Continue;
        }

        return RetStop();
    }

    [GameEventHandler]
    public HookResult OnPostItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        var pawnHandle = player?.PlayerPawn;

        if (Helpers.IsWarmup())
        {
            return HookResult.Continue;
        }

        if (!Helpers.PlayerIsValid(player) || pawnHandle is null || !pawnHandle.IsValid)
        {
            return HookResult.Continue;
        }

        var controller = player!;
        var item = Utils.ToEnum<CsItem>(@event.Weapon);
        var team = controller.Team;
        var playerId = Helpers.GetSteamId(controller);
        var isPreferred = WeaponHelpers.IsPreferred(team, item);

        var purchasedAllocationType = RoundTypeManager.Instance.GetCurrentRoundType() is not null
            ? WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                RoundTypeManager.Instance.GetCurrentRoundType(), team, item
            )
            : null;

        var isValidAllocation = WeaponHelpers.IsAllocationTypeValidForRound(purchasedAllocationType,
            RoundTypeManager.Instance.GetCurrentRoundType()) && WeaponHelpers.IsUsableWeapon(item);

        Log.Debug($"item {item} team {team} player {playerId}");
        Log.Debug($"weapon alloc {purchasedAllocationType} valid? {isValidAllocation}");
        Log.Debug($"Preferred? {isPreferred}");

        if (
            Helpers.IsWeaponAllocationAllowed() &&
            // Preferred weapons are treated like un-buy-able weapons, but at the end we'll set the user preference
            !isPreferred &&
            isValidAllocation &&
            // redundant, just for null checker
            purchasedAllocationType is not null
        )
        {
            // Update cache first for immediate consistency
            // DB persistence handled by periodic cache flush (every 120s)
            PlayerSettingsCache.SetWeaponPreference(playerId, team, purchasedAllocationType.Value, item);
            var slotType = WeaponHelpers.GetSlotTypeForItem(item);
            if (slotType is not null)
            {
                SetPlayerRoundAllocation(controller, slotType.Value, item);
            }
            else
            {
                Log.Debug($"WARN: No slot for {item}");
            }
        }
        else
        {
            var removedAnyWeapons = Helpers.RemoveWeapons(controller,
                i =>
                {
                    if (!WeaponHelpers.IsWeapon(i))
                    {
                        return i == item;
                    }

                    if (RoundTypeManager.Instance.GetCurrentRoundType() is null)
                    {
                        return true;
                    }

                    var at = WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                        RoundTypeManager.Instance.GetCurrentRoundType(), team, i);
                    Log.Trace($"at: {at}");
                    return at is null || at == purchasedAllocationType;
                });
            Log.Debug($"Removed {item}? {removedAnyWeapons}");

            var replacementSlot = RoundTypeManager.Instance.GetCurrentRoundType() == RoundType.Pistol
                ? ItemSlotType.Secondary
                : ItemSlotType.Primary;

            var replacedWeapon = false;
            var slotToSelect = WeaponHelpers.GetSlotNameForSlotType(replacementSlot);
            if (removedAnyWeapons && RoundTypeManager.Instance.GetCurrentRoundType() is not null &&
                WeaponHelpers.IsWeapon(item))
            {
                var replacementAllocationType =
                    WeaponHelpers.GetReplacementWeaponAllocationTypeForWeapon(RoundTypeManager.Instance
                        .GetCurrentRoundType());
                Log.Debug($"Replacement allocation type {replacementAllocationType}");
                if (replacementAllocationType is not null)
                {
                    var replacementItem = GetPlayerRoundAllocation(controller, replacementSlot);
                    Log.Debug($"Replacement item {replacementItem} for slot {replacementSlot}");
                    if (replacementItem is not null)
                    {
                        replacedWeapon = true;
                        AllocateItemsForPlayer(controller, new List<CsItem>
                        {
                            replacementItem.Value
                        }, slotToSelect);
                    }
                }
            }

            if (!replacedWeapon)
            {
                AddTimer(0.1f, () =>
                {
                    if (Helpers.PlayerIsValid(controller) && controller.UserId is not null)
                    {
                        NativeAPI.IssueClientCommand((int)controller.UserId, slotToSelect);
                    }
                });
            }
        }

        var playerPos = controller.PlayerPawn?.Value?.AbsOrigin;

        var pEntity = new CEntityIdentity(EntitySystem.FirstActiveEntity);
        for (; pEntity is not null && pEntity.Handle != IntPtr.Zero; pEntity = pEntity.Next)
        {
            var p = Utilities.GetEntityFromIndex<CBasePlayerWeapon>((int)pEntity.EntityInstance.Index);
            if (p is null)
            {
                continue;
            }
            if (
                !p.IsValid ||
                !p.DesignerName.StartsWith("weapon") ||
                p.DesignerName.Equals("weapon_c4") ||
                playerPos is null ||
                p.AbsOrigin is null
            )
            {
                continue;
            }

            var distance = Helpers.GetVectorDistance(playerPos, p.AbsOrigin);
            if (distance < 30)
            {
                AddTimer(.5f, () =>
                {
                    if (p.IsValid && !p.OwnerEntity.IsValid)
                    {
                        Log.Trace($"Removing {p.DesignerName}");
                        p.Remove();
                    }
                });
            }
        }

        if (isPreferred)
        {
            var itemName = Enum.GetName(item);
            if (itemName is not null)
            {
                // Use cache-based handler for consistency
                var message = OnWeaponCommandHelper.HandleFromCache(
                    new List<string> { itemName },
                    Helpers.GetSteamId(controller),
                    RoundTypeManager.Instance.GetCurrentRoundType(),
                    team,
                    false,
                    out _
                );
                Helpers.WriteNewlineDelimited(message, controller.PrintToChat);
            }
        }

        return HookResult.Continue;
    }

    private void HandleAllocateEvent()
    {
        IsAllocatingForRound = true;
        Log.Debug($"Handling allocate event");
        Server.ExecuteCommand("mp_max_armor 0");

        var menu = _allocatorMenuManager.GetMenu<VoteMenu>(MenuType.NextRoundVote);
        menu.GatherAndHandleVotes();

        var allPlayers = Utilities.GetPlayers()
            .Where(player => Helpers.PlayerIsValid(player) && player.Connected == PlayerConnectedState.PlayerConnected)
            .ToList();

        OnRoundPostStartHelper.Handle(
            allPlayers,
            Helpers.GetSteamId,
            Helpers.GetTeam,
            GiveDefuseKit,
            AllocateItemsForPlayer,
            Helpers.HasAwpPermission,
            Helpers.HasSsgPermission,
            Helpers.HasEnemyStuffPermission,
            out var currentRoundType
        );
        RoundTypeManager.Instance.SetCurrentRoundType(currentRoundType);
        RoundTypeManager.Instance.SetNextRoundTypeOverride(null);

        switch (currentRoundType)
        {
            case RoundType.Pistol:
                {
                    Server.ExecuteCommand("execifexists cs2-retakes/Pistol.cfg");
                    break;
                }
            case RoundType.HalfBuy:
                {
                    Server.ExecuteCommand("execifexists cs2-retakes/SmallBuy.cfg");
                    break;
                }
            case RoundType.FullBuy:
                {
                    Server.ExecuteCommand("execifexists cs2-retakes/FullBuy.cfg");
                    break;
                }
        }

        if (Configs.GetConfigData().EnableRoundTypeAnnouncement)
        {
            var roundType = RoundTypeManager.Instance.GetCurrentRoundType()!.Value;
            var roundTypeName = RoundTypeHelpers.TranslateRoundTypeName(roundType);
            var message = Translator.Instance["announcement.roundtype", roundTypeName];
            Server.PrintToChatAll($"{MessagePrefix}{message}");
            if (Configs.GetConfigData().EnableRoundTypeAnnouncementCenter)
            {
                foreach (var player in allPlayers)
                {
                    player.PrintToCenter(
                        $"{MessagePrefix}{Translator.Instance["center.announcement.roundtype", roundTypeName]}");
                }
            }
        }

        AddTimer(.5f, () =>
        {
            Log.Debug("Turning off round allocation");
            IsAllocatingForRound = false;
        });
    }

    public void OnTick()
    {
        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.OnTick();
        }

        if (_announceBombsite)
        {
            // Use cached counts (set when announcement starts) - avoid per-tick GetPlayers calls
            var showToCTOnly = Configs.GetConfigData().BombSiteAnnouncementCenterToCTOnly;

            // Single GetPlayers call per tick instead of two Count() + FindAllEntities
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid || !player.PawnIsAlive || player.IsBot || player.IsHLTV) continue;

                if (player.TeamNum == (byte)CsTeam.Terrorist && !showToCTOnly)
                {
                    player.PrintToCenterHtml(
                        string.Format(Localizer["T.Message"], _bombsite, _cachedBombsiteImage, _cachedTCount, _cachedCtCount));
                }
                else if (player.TeamNum == (byte)CsTeam.CounterTerrorist)
                {
                    player.PrintToCenterHtml(
                        string.Format(Localizer["CT.Message"], _bombsite, _cachedBombsiteImage, _cachedTCount, _cachedCtCount));
                }
            }
        }
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnEventBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (Configs.GetConfigData().DisableDefaultBombPlantedCenterMessage)
        {
            info.DontBroadcast = true;
        }

        if (Configs.GetConfigData().ForceCloseBombSiteAnnouncementCenterOnPlant)
        {
            _bombsite = "";
            _announceBombsite = false;
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;
        _bombsiteAnnounceOneTime = false;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;
        _bombsite = "";
        _announceBombsite = false;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventEnterBombzone(EventEnterBombzone @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null || Helpers.IsWarmup() || _bombsiteAnnounceOneTime) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.TeamNum != (byte)CsTeam.Terrorist) return HookResult.Continue;

        var playerPawn = player.PlayerPawn;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (playerPawn == null || !playerPawn.IsValid) return HookResult.Continue;

        var playerPosition = playerPawn.Value!.AbsOrigin;

        foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBombTarget>("info_bomb_target"))
        {
            var entityPosition = entity.AbsOrigin;
            if (entityPosition != null)
            {
                var distanceVector = playerPosition! - entityPosition;
                var distance = distanceVector.Length();
                float thresholdDistance = 400.0f;

                if (distance <= thresholdDistance)
                {
                    if (entity.DesignerName == "info_bomb_target_hint_A")
                    {
                        _bombsite = "A";
                        if (Configs.GetConfigData().EnableBombSiteAnnouncementCenter)
                        {
                            Server.NextFrame(() =>
                            {
                                AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterDelay, () =>
                                {
                                    _bombsiteAnnounceOneTime = true;
                                    // Cache player counts and image once for OnTick optimization
                                    _cachedBombsiteImage = Translator.Instance["BombSite.A"];
                                    var players = Utilities.GetPlayers();
                                    _cachedCtCount = players.Count(p => p.TeamNum == (int)CsTeam.CounterTerrorist && p.PawnIsAlive && !p.IsHLTV);
                                    _cachedTCount = players.Count(p => p.TeamNum == (int)CsTeam.Terrorist && p.PawnIsAlive && !p.IsHLTV);
                                    _announceBombsite = true;
                                    AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterShowTimer, () =>
                                    {
                                        _bombsite = "";
                                        _announceBombsite = false;
                                    }, TimerFlags.STOP_ON_MAPCHANGE);
                                }, TimerFlags.STOP_ON_MAPCHANGE);
                            });
                        }

                        if (Configs.GetConfigData().EnableBombSiteAnnouncementChat)
                        {
                            Server.PrintToChatAll(Localizer["chatAsite.line1"]);
                            Server.PrintToChatAll(Localizer["chatAsite.line2"]);
                            Server.PrintToChatAll(Localizer["chatAsite.line3"]);
                            Server.PrintToChatAll(Localizer["chatAsite.line4"]);
                            Server.PrintToChatAll(Localizer["chatAsite.line5"]);
                            Server.PrintToChatAll(Localizer["chatAsite.line6"]);
                        }

                        break;
                    }
                    else if (entity.DesignerName == "info_bomb_target_hint_B")
                    {
                        _bombsite = "B";
                        if (Configs.GetConfigData().EnableBombSiteAnnouncementCenter)
                        {
                            Server.NextFrame(() =>
                            {
                                AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterDelay, () =>
                                {
                                    _bombsiteAnnounceOneTime = true;
                                    // Cache player counts and image once for OnTick optimization
                                    _cachedBombsiteImage = Translator.Instance["BombSite.B"];
                                    var players = Utilities.GetPlayers();
                                    _cachedCtCount = players.Count(p => p.TeamNum == (int)CsTeam.CounterTerrorist && p.PawnIsAlive && !p.IsHLTV);
                                    _cachedTCount = players.Count(p => p.TeamNum == (int)CsTeam.Terrorist && p.PawnIsAlive && !p.IsHLTV);
                                    _announceBombsite = true;
                                    AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterShowTimer, () =>
                                    {
                                        _bombsite = "";
                                        _announceBombsite = false;
                                    }, TimerFlags.STOP_ON_MAPCHANGE);
                                }, TimerFlags.STOP_ON_MAPCHANGE);
                            });
                        }

                        if (Configs.GetConfigData().EnableBombSiteAnnouncementChat)
                        {
                            Server.PrintToChatAll(Localizer["chatBsite.line1"]);
                            Server.PrintToChatAll(Localizer["chatBsite.line2"]);
                            Server.PrintToChatAll(Localizer["chatBsite.line3"]);
                            Server.PrintToChatAll(Localizer["chatBsite.line4"]);
                            Server.PrintToChatAll(Localizer["chatBsite.line5"]);
                            Server.PrintToChatAll(Localizer["chatBsite.line6"]);
                        }

                        break;
                    }
                }
            }
        }

        return HookResult.Continue;
    }


    [GameEventHandler]
    public HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.OnEventPlayerDisconnect(@event, info);
        }

        // Flush player settings to database on disconnect
        var player = @event.Userid;
        if (player != null && player.IsValid)
        {
            var steamId = Helpers.GetSteamId(player);
            if (steamId != 0)
            {
                _ = Task.Run(() => PlayerSettingsCache.FlushPlayerAsync(steamId));
            }
        }

        return HookResult.Continue;
    }

    // ReSharper disable once RedundantArgumentDefaultValue
    [GameEventHandler(HookMode.Post)]
    public HookResult OnEventPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.OnEventPlayerChat(@event, info);
        }

        var eventplayer = @event.Userid;
        var eventmessage = @event.Text;
        var player = Utilities.GetPlayerFromUserid(eventplayer);

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (player == null || !player.IsValid) return HookResult.Continue;

        if (string.IsNullOrWhiteSpace(eventmessage)) return HookResult.Continue;
        string trimmedMessageStart = eventmessage.TrimStart();
        string message = trimmedMessageStart.TrimEnd();

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnEventRoundAnnounceWarmup(EventRoundAnnounceWarmup @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (Configs.GetConfigData().ResetStateOnGameRestart)
        {
            ResetState();
        }

        return HookResult.Continue;
    }

    #endregion

    #region Helpers

    private void SetPlayerRoundAllocation(CCSPlayerController player, ItemSlotType slotType, CsItem item)
    {
        if (!_allocatedPlayerItems.TryGetValue(player, out _))
        {
            _allocatedPlayerItems[player] = new();
        }

        _allocatedPlayerItems[player][slotType] = item;
        Log.Trace($"Round allocation for player {player.Slot} {slotType} {item}");
    }

    private CsItem? GetPlayerRoundAllocation(CCSPlayerController player, ItemSlotType? slotType)
    {
        if (slotType is null || !_allocatedPlayerItems.TryGetValue(player, out var playerItems))
        {
            return null;
        }

        if (playerItems.TryGetValue(slotType.Value, out var localReplacementItem))
        {
            return localReplacementItem;
        }

        return null;
    }

    private void AllocateItemsForPlayer(CCSPlayerController player, ICollection<CsItem> items, string? slotToSelect)
    {
        Log.Trace($"Allocating items: {string.Join(",", items)}; selecting slot {slotToSelect}");

        AddTimer(0.1f, () =>
        {
            if (!Helpers.PlayerIsValid(player) || !player.PawnIsAlive || player.PlayerPawn is null || !player.PlayerPawn.IsValid || player.PlayerPawn.Value is null)
            {
                Log.Trace("Player is not valid when allocating item");
                return;
            }

            foreach (var item in items)
            {
                string? itemString = EnumUtils.GetEnumMemberAttributeValue(item);
                if (string.IsNullOrWhiteSpace(itemString))
                {
                    continue;
                }

                if (Configs.GetConfigData().CapabilityWeaponPaints && CustomFunctions != null && CustomFunctions.PlayerGiveNamedItemEnabled())
                {
                    CustomFunctions?.PlayerGiveNamedItem(player, itemString);
                }
                else
                {
                    player.GiveNamedItem(itemString);
                }

                var slotType = WeaponHelpers.GetSlotTypeForItem(item);
                if (slotType is not null)
                {
                    SetPlayerRoundAllocation(player, slotType.Value, item);
                }
            }

            if (slotToSelect is not null)
            {
                AddTimer(0.1f, () =>
                {
                    if (Helpers.PlayerIsValid(player) && player.PawnIsAlive && player.UserId is not null)
                    {
                        NativeAPI.IssueClientCommand((int)player.UserId, slotToSelect);
                    }
                });
            }
        });
    }

    private void GiveDefuseKit(CCSPlayerController player)
    {
        AddTimer(0.1f, () =>
        {
            if (!Helpers.PlayerIsValid(player) || !player.PlayerPawn.IsValid || player.PlayerPawn.Value is null ||
                !player.PlayerPawn.Value.IsValid || player.PlayerPawn.Value?.ItemServices?.Handle is null)
            {
                Log.Trace($"Player is not valid when giving defuse kit");
                return;
            }

            var itemServices = new CCSPlayer_ItemServices(player.PlayerPawn.Value.ItemServices.Handle);
            itemServices.HasDefuser = true;
        });
    }

    #endregion
}
