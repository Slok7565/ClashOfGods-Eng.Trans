using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AmongUs.GameOptions;
using COG.Config.Impl;
using COG.Constant;
using COG.Game.CustomWinner;
using COG.Listener.Event;
using COG.Listener.Event.Impl.AuClient;
using COG.Listener.Event.Impl.Game;
using COG.Listener.Event.Impl.GSManager;
using COG.Listener.Event.Impl.HManager;
using COG.Listener.Event.Impl.ICutscene;
using COG.Listener.Event.Impl.Player;
using COG.Listener.Event.Impl.RManager;
using COG.Listener.Event.Impl.TPBehaviour;
using COG.Listener.Event.Impl.VentImpl;
using COG.Role;
using COG.Role.Impl.Crewmate;
using COG.Rpc;
using COG.UI.CustomGameObject.Arrow;
using COG.Utils;
using Il2CppSystem.Collections;
using InnerNet;
using UnityEngine;
using Action = Il2CppSystem.Action;
using Debug = System.Diagnostics.Debug;
using GameStates = COG.States.GameStates;
using Random = System.Random;

namespace COG.Listener.Impl;

public class GameListener : IListener
{
    [EventHandler(EventHandlerType.Prefix)]
    public void OnRpcReceived(PlayerHandleRpcEvent @event)
    {
        var callId = @event.CallId;
        var reader = @event.Reader;
        var knownRpc = (KnownRpc)callId;

        switch (knownRpc)
        {
            case KnownRpc.ShareRoles:
            {
                if (AmongUsClient.Instance.AmHost) return;
                // 清除原列表，防止干扰
                GameUtils.PlayerData.Clear();
                // 开始读入数据
                Main.Logger.LogDebug("The role data from the host was received by us.");

                var originalText = reader.ReadString()!;
                foreach (var s in originalText.Split(","))
                {
                    var texts = s.Split("|");
                    var player = PlayerUtils.GetPlayerById(Convert.ToByte(texts[0]));
                    var role = CustomRoleManager.GetManager().GetRoleById(Convert.ToInt32(texts[1]));
                    player!.SetCustomRole(role!);
                }

                foreach (var playerRole in GameUtils.PlayerData)
                    Main.Logger.LogInfo($"{playerRole.Player.name}({playerRole.Player.Data.FriendCode})" +
                                        $" => {playerRole.Role.Name}");

                break;
            }
            case KnownRpc.Revive:
            {
                // 从Rpc中读入PlayerControl
                var target = reader.ReadNetObject<PlayerControl>();
                
                // 复活目标玩家
                target.Revive();
                break;
            }
            case KnownRpc.CleanDeadBody:
            {
                if (!GameStates.InGame) return;
                var pid = reader.ReadByte();
                var body = Object.FindObjectsOfType<DeadBody>().ToList().FirstOrDefault(b => b.ParentId == pid);
                if (!body) return;
                body!.gameObject.SetActive(false);
                break;
            }
            case KnownRpc.Mark:
            {
                var target = reader.ReadNetObject<PlayerControl>();
                var tag = reader.ReadString();
                
                var playerData = target.GetPlayerData();
                playerData?.Tags.Add(tag);
                break;
            }
        }
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void AfterPlayerFixedUpdate(PlayerFixedUpdateEvent @event)
    {
        var player = @event.Player;
        if (player == null! || !PlayerControl.LocalPlayer) return;
        if (GameStates.IsLobby && AmongUsClient.Instance.AmHost)
        {
            var mainOption = GameUtils.GetGameOptions();
            var roleOption = mainOption.RoleOptions;

            var changed = false;

            foreach (var role in Enum.GetValues<RoleTypes>())
            {
                if (roleOption.GetNumPerGame(role) != 0 || roleOption.GetChancePerGame(role) != 0)
                {
                    roleOption.SetRoleRate(role, 0, 0);
                    changed = true;
                }
            }

            if (mainOption.RulesPreset != RulesPresets.Custom)
            {
                mainOption.RulesPreset = RulesPresets.Custom;
                changed = true;
            }

            if (changed)
            {
                GameManager.Instance.LogicOptions.SyncOptions();
            }
        }

        if (PlayerControl.LocalPlayer.IsSamePlayer(player))
        {
            var playerRole = player.GetPlayerData();
            if (playerRole is null) return;

            var subRoles = playerRole.SubRoles;
            var mainRole = playerRole.Role;
            var nameText = player.cosmetics.nameText;
            nameText.color = mainRole.Color;

            var nameTextBuilder = new StringBuilder();
            var subRoleNameBuilder = new StringBuilder();

            if (!subRoles.SequenceEqual(Array.Empty<CustomRole>()))
                foreach (var role in subRoles)
                    subRoleNameBuilder.Append(' ').Append(role.GetColorName());

            nameTextBuilder.Append(mainRole.Name)
                .Append(subRoleNameBuilder)
                .Append('\n').Append(player.Data.PlayerName);

            var adtnalTextBuilder = new StringBuilder();
            foreach (var (color, text) in subRoles.ToList()
                         .Select(r => (
                             r.Color,
                             r.HandleAdditionalPlayerName()
                         )))
                adtnalTextBuilder.Append(' ').Append(text.Color(color));

            nameTextBuilder.Append(adtnalTextBuilder);

            nameText.text = nameTextBuilder + adtnalTextBuilder.ToString();
        }
    }

    public static void SelectRoles()
    {
        GameUtils.PlayerData.Clear(); // 首先清除 防止干扰

        if (!AmongUsClient.Instance.AmHost) return; // 不是房主停止分配

        // 开始分配职业
        var players = PlayerUtils.GetAllPlayers().ToList().Disarrange(); // 打乱玩家顺序

        var rolesToAdd = new List<CustomRole>(); // 新建集合，用来存储可用的职业

        // 获取最大可以启用的内鬼数量
        var maxImpostorsNum = GameUtils.GetImpostorsNum();

        // 新建一个获取器
        var getter = CustomRoleManager.GetManager().NewGetter();

        // 获取已经获取的内鬼职业数量
        var equalsImpostorsNum = 0;

        // 开始获取可以分配的职业
        while (getter.HasNext())
        {
            var next = getter.GetNext();
            if (next == null) continue;
            if (rolesToAdd.Count >= players.Count) break;
            if (equalsImpostorsNum >= maxImpostorsNum && next.CampType == CampType.Impostor) continue;
            if (next.CampType == CampType.Impostor) equalsImpostorsNum++;
            rolesToAdd.Add(next);
        }

        // 新建副职业获取器
        var subRoleGetter = CustomRoleManager.GetManager().NewGetter(true);

        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            CustomRole role;
            try
            {
                role = rolesToAdd[i];
            }
            catch
            {
                role = CustomRoleManager.GetManager().GetTypeRoleInstance<Crewmate>(); // 无法分配默认职业为Crewmate
            }

            // 接下来分配副职业
            var random = new Random();
            var option = GlobalCustomOptionConstant.MaxSubRoleNumber;
            Debug.Assert(option != null, nameof(option) + " != null");
            var subRoleNumber = random.Next(0, option.GetInt()); // 获取这个玩家应当被给予的副职业数量
            var subRoles = new List<CustomRole>();
            var givenNumber = 0;

            while (subRoleGetter.HasNext())
            {
                if (subRoleNumber < givenNumber) break;
                givenNumber++;
                var toAdd = subRoleGetter.GetNext();
                if (toAdd == null) break;
                subRoles.Add(toAdd);
            }

            player.SetCustomRole(role, subRoles.ToArray());
        }

        // 打印职业分配信息
        foreach (var playerRole in GameUtils.PlayerData)
            Main.Logger.LogInfo($"{playerRole.Player.name}({playerRole.Player.Data.FriendCode})" +
                                $" => {playerRole.Role.Name}");
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnSelectRoles(RoleManagerSelectRolesEvent _)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        Main.Logger.LogInfo("Shhhhhh!");
        
        Main.Logger.LogInfo("Select roles for players...");
        SelectRoles();

        Main.Logger.LogInfo("Share roles for players...");
        ShareRoles();

        var roleList = GameUtils.PlayerData.Select(pr => pr.Role).ToList();
        roleList.AddRange(GameUtils.PlayerData.SelectMany(pr => pr.SubRoles));

        foreach (var availableRole in roleList) availableRole.AfterSharingRoles();
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnJoinLobby(GameStartManagerStartEvent @event)
    {
        var manager = @event.GameStartManager;
        var privateButton = manager.HostPrivacyButtons.transform.FindChild("PRIVATE BUTTON");
        var inactive = privateButton.FindChild("Inactive").GetComponent<SpriteRenderer>();
        var highlight = privateButton.FindChild("Highlight").GetComponent<SpriteRenderer>();
        inactive.color = highlight.color = Palette.DisabledGrey;
    }

    [EventHandler(EventHandlerType.Prefix)]
    public bool OnMakePublic(GameStartManagerMakePublicEvent _)
    {
        if (!AmongUsClient.Instance.AmHost) return false;
        GameUtils.SendGameMessage(LanguageConfig.Instance.MakePublicMessage);
        // 禁止设置为公开
        return false;
    }

    [EventHandler(EventHandlerType.Prefix)]
    public bool OnSetUpRoleText(IntroCutsceneShowRoleEvent @event)
    {
        var intro = @event.IntroCutscene;
        Main.Logger.LogInfo("Setup role text for the player...");

        var myRole = GameUtils.GetLocalPlayerRole();

        var list = new List<IEnumerator>();

        void SetupRoles()
        {
            if (GameOptionsManager.Instance.currentGameMode != GameModes.Normal) return;
            intro.RoleText.text = myRole.Name;
            intro.RoleText.color = myRole.Color;

            var sb = new StringBuilder(myRole.GetColorName());
            foreach (var sub in PlayerControl.LocalPlayer.GetSubRoles())
                sb.Append(" + ").Append(sub.GetColorName());

            intro.YouAreText.text = sb.ToString();
            intro.YouAreText.color = myRole.Color;
            intro.RoleBlurbText.color = myRole.Color;
            intro.RoleBlurbText.text = myRole.ShortDescription;

            intro.YouAreText.gameObject.SetActive(true);
            intro.RoleText.gameObject.SetActive(true);
            intro.RoleBlurbText.gameObject.SetActive(true);

            SoundManager.Instance.PlaySound(PlayerControl.LocalPlayer.Data.Role.IntroSound, false);

            if (intro.ourCrewmate == null)
            {
                intro.ourCrewmate = intro.CreatePlayer(0, 1, PlayerControl.LocalPlayer.Data, false);
                intro.ourCrewmate.gameObject.SetActive(false);
            }

            intro.ourCrewmate.gameObject.SetActive(true);
            var transform = intro.ourCrewmate.transform;
            transform.localPosition = new Vector3(0f, -1.05f, -18f);
            transform.localScale = new Vector3(1f, 1f, 1f);
            intro.ourCrewmate.ToggleName(false);
        }

        list.Add(Effects.Action((Action)SetupRoles));
        list.Add(Effects.Wait(2.5f));

        void Action()
        {
            intro.YouAreText.gameObject.SetActive(false);
            intro.RoleText.gameObject.SetActive(false);
            intro.RoleBlurbText.gameObject.SetActive(false);
            intro.ourCrewmate.gameObject.SetActive(false);
        }

        list.Add(Effects.Action((Action)Action));

        @event.SetResult(Effects.Sequence(list.ToArray()));

        return false;
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnIntroDestroy(IntroCutsceneDestroyEvent @event)
    {
        var intro = @event.IntroCutscene;
        PlayerUtils.PoolablePlayerPrefab = Object.Instantiate(intro.PlayerPrefab);
        PlayerUtils.PoolablePlayerPrefab.gameObject.SetActive(false);
    }

    [EventHandler(EventHandlerType.Prefix)]
    public void OnSetUpTeamText(IntroCutsceneBeginCrewmateEvent @event)
    {
        var role = GameUtils.GetLocalPlayerRole();
        var player = PlayerControl.LocalPlayer;

        var camp = role?.CampType;
        if (camp is not (CampType.Neutral or CampType.Unknown)) return;
        var soloTeam = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
        soloTeam.Add(player);
        @event.SetTeamToDisplay(soloTeam);
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void AfterSetUpTeamText(IntroCutsceneBeginCrewmateEvent @event)
    {
        var intro = @event.IntroCutscene;
        var role = GameUtils.GetLocalPlayerRole();

        var camp = role.CampType;

        intro.BackgroundBar.material.color = camp.GetColor();
        intro.TeamTitle.text = camp.GetName();
        intro.TeamTitle.color = camp.GetColor();

        intro.ImpostorText.text = camp.GetDescription();
    }

    [EventHandler(EventHandlerType.Prefix)]
    public bool OnCheckGameEnd(GameCheckEndEvent _)
    {
        return CustomWinnerManager.CheckEndForCustomWinners();
    }

    [EventHandler(EventHandlerType.Prefix)]
    public bool OnPlayerVent(VentCheckEvent @event)
    {
        var playerInfo = @event.PlayerInfo;
        foreach (var ventAble in from playerRole in GameUtils.PlayerData
                 where playerRole.Player.Data.IsSamePlayer(playerInfo)
                 select playerRole.Role.CanVent)
        {
            @event.SetCanUse(ventAble);
            @event.SetCouldUse(ventAble);
            @event.SetResult(float.MaxValue);
            return ventAble;
        }

        return true;
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnHudUpdate(HudManagerUpdateEvent @event)
    {
        var manager = @event.Manager;
        var role = GameUtils.GetLocalPlayerRole();

        manager.KillButton.SetDisabled();
        manager.KillButton.ToggleVisible(false);
        manager.KillButton.gameObject.SetActive(false);

        if (!role.CanVent)
        {
            manager.ImpostorVentButton.SetDisabled();
            manager.ImpostorVentButton.ToggleVisible(false);
            manager.ImpostorVentButton.gameObject.SetActive(false);
        }

        if (!role.CanSabotage)
        {
            manager.SabotageButton.SetDisabled();
            manager.SabotageButton.ToggleVisible(false);
            manager.SabotageButton.gameObject.SetActive(false);
        }

        Arrow.CreatedArrows.RemoveAll(a => !a.ArrowObject);
        Arrow.CreatedArrows.ForEach(a => a.Update());
    }

    private static void ShareRoles()
    {
        var writer = RpcUtils.StartRpcImmediately(PlayerControl.LocalPlayer, (byte)KnownRpc.ShareRoles);

        var sb = new StringBuilder();

        for (var i = 0; i < GameUtils.PlayerData.Count; i++)
        {
            var playerRole = GameUtils.PlayerData[i];
            sb.Append(playerRole.Player.PlayerId + "|" + playerRole.Role.Id);

            if (i + 1 < GameUtils.PlayerData.Count) sb.Append(',');
        }

        writer.Write(sb.ToString());

        // 职业格式应该是
        // playerId1|roleId1,playerId2|roleId2 

        writer.Finish();
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnBeginExile(PlayerExileBeginEvent @event)
    {
        if (!GameUtils.GetGameOptions().ConfirmImpostor) return;

        var controller = @event.ExileController;
        var player = @event.Player;
        if (!player) return;

        var role = player.GetMainRole();

        int GetCount(IEnumerable<PlayerData> list)
        {
            return list.Select(p => p.Player)
                .Where(p => !p.IsSamePlayer(player) && p.IsAlive()).ToList().Count;
        }

        var crewCount = GetCount(PlayerUtils.AllCrewmates);
        var impCount = GetCount(PlayerUtils.AllImpostors);
        var neutralCount = GetCount(PlayerUtils.AllNeutrals);

        var roleText = controller.completeString = role.HandleEjectText(player);
        var playerInfoText = controller.ImpostorText.text =
            LanguageConfig.Instance.AlivePlayerInfo.CustomFormat(crewCount, neutralCount, impCount);

        Main.Logger.LogInfo($"Eject text: {roleText} & {playerInfoText}");
    }

    private readonly List<Handler> _handlers = new();

    [EventHandler(EventHandlerType.Postfix)]
    public void OnGameStart(GameStartEvent _)
    {
        Main.Logger.LogInfo("Game started!");

        GameStates.InGame = true;
        
        if (!_handlers.IsEmpty())
        {
            ListenerManager.GetManager().UnRegisterHandlers(_handlers.ToArray());
            _handlers.Clear();
        }
        
        CustomRoleManager.GetManager().GetRoles().Select(role => role.GetListener()).ForEach(
            listener => _handlers.AddRange(ListenerManager.GetManager().AsHandlers(listener)));
        
        ListenerManager.GetManager().RegisterHandlers(_handlers.ToArray());

        if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
            foreach (var player in PlayerControl.AllPlayerControls)
                player.RpcSetCustomRole<Crewmate>();
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnTaskPanelSetText(TaskPanelBehaviourSetTaskTextEvent @event)
    {
        var originText = @event.GetTaskString();
        var localRole = GameUtils.GetLocalPlayerRole();
        if (originText == "None" || localRole == null) return;

        var sb = new StringBuilder();

        sb.Append(localRole.GetColorName()).Append('：').Append(localRole.ShortDescription.Color(localRole.Color))
            .Append("\r\n\r\n");

        /*
            <color=#FF0000FF>进行破坏，将所有人杀死。
            <color=#FF1919FF>假任务：</color></color>
        */

        var impTaskText = TranslationController.Instance.GetString(StringNames.ImpostorTask); // 进行破坏，将所有人杀死。
        var fakeTaskText = TranslationController.Instance.GetString(StringNames.FakeTasks); // 假任务：
        var impTaskTextFull =
            $"<color=#FF0000FF>{impTaskText}\r\n<color=#FF1919FF>{fakeTaskText}</color></color>\r\n";

        if (originText.StartsWith(impTaskTextFull))
        {
            var idx = originText.IndexOf(impTaskTextFull, StringComparison.Ordinal) + impTaskTextFull.Length;
            sb.Append($"<color=#FF1919FF>{fakeTaskText}</color>\r\n").Append(originText[idx..]);
        }
        else
        {
            sb.Append(originText);
        }

        @event.SetTaskString(sb.ToString());
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnGameEnd(AmongUsClientGameEndEvent _)
    {
        EndGameResult.CachedWinners.Clear();
        CustomWinnerManager.AllWinners.ToArray()
            .ForEach(p => EndGameResult.CachedWinners.Add(new CachedPlayerData(p.Data)));
    }
}