using System.Collections;
using System.Collections.Generic;
using System.Linq;
using COG.Config.Impl;
using COG.Game.CustomWinner;
using COG.Listener;
using COG.Listener.Event.Impl.Player;
using COG.Role.Impl.Impostor;
using COG.Rpc;
using COG.UI.CustomButton;
using COG.UI.CustomGameObject.Arrow;
using COG.UI.CustomOption;
using COG.UI.CustomOption.ValueRules.Impl;
using COG.Utils;
using Reactor.Utilities;
using UnityEngine;
using Random = System.Random;

namespace COG.Role.Impl.Neutral;

public class Vulture : CustomRole, IListener, IWinnable
{
    public Vulture() : base(Color.blue, CampType.Neutral)
    {
        if (!ShowInOptions) throw new System.Exception("vulture is not allowed to show in options");
        EatingCooldown = CreateOption(() => LanguageConfig.Instance.VultureEatCooldown,
            new FloatOptionValueRule(10f, 5f, 60f, 30f));
        WinningEatenCount = CreateOption(() => LanguageConfig.Instance.VultureEatenCountToWin,
            new IntOptionValueRule(2, 1, 6, 4));
        HasArrowToBodies = CreateOption(() => LanguageConfig.Instance.VultureHasArrowToBodies,
            new BoolOptionValueRule(true));

        EatButton = CustomButton.Create(
            () =>
            {
                ClosestBody = PlayerUtils.GetClosestBody();
                var arrow = Arrows.FirstOrDefault(a => a.Target == ClosestBody!.transform.position);

                if (arrow != null)
                {
                    Arrows.Remove(arrow);
                    arrow.Destroy();
                }

                RpcEatBody(ClosestBody!);
            },
            () => EatButton?.ResetCooldown(),
            () => ClosestBody,
            () => true,
            null!,
            2,
            KeyCode.E,
            LanguageConfig.Instance.EatAction,
            () => EatingCooldown?.GetFloat() ?? 30f,
            0
        );
        AddButton(EatButton);

        EatenCount = 0;
    }

    public CustomOption EatingCooldown { get; }
    public CustomOption WinningEatenCount { get; }
    public CustomOption HasArrowToBodies { get; }
    public CustomButton EatButton { get; }
    public static int EatenCount { get; private set; }
    private static DeadBody? ClosestBody { get; set; }
    public static List<Arrow> Arrows { get; } = new();

    public ulong GetWeight()
    {
        return IWinnable.GetOrder(6);
    }

    public bool CanWin() // 只有房主会判断游戏胜利，因此玩家之间需要同步数据
    {
        if (EatenCount >= (WinningEatenCount?.GetInt() ?? 4))
        {
            CustomWinnerManager.EndGame(Players, "Vulture wins", Color);
            return true;
        }

        return false;
    }

    public void RpcEatBody(DeadBody body)
    {
        RpcUtils.StartRpcImmediately(PlayerControl.LocalPlayer, KnownRpc.EatBody)
            .Write(body.ParentId).WritePacked(EatenCount)
            .Finish();
        EatBody(body);
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnPlayerDeath(PlayerMurderEvent _)
    {
        Coroutines.Start(CoFadeDeath());
    }

    public IEnumerator CoFadeDeath()
    {
        yield return HudManager.Instance.CoFadeFullScreen(new Color(0, 0, 0, 0), Palette.CrewmateBlue);
        yield return HudManager.Instance.CoFadeFullScreen(Palette.CrewmateBlue, new Color(0, 0, 0, 0));
    }

    public void EatBody(DeadBody body)
    {
        body.gameObject.SetActive(false);
        EatenCount++;
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnReceiveRpc(PlayerHandleRpcEvent @event)
    {
        if ((KnownRpc)@event.CallId != KnownRpc.EatBody) return;

        var reader = @event.Reader;

        var pid = reader.ReadByte();
        var body = Object.FindObjectsOfType<DeadBody>().ToList().FirstOrDefault(b => b.ParentId == pid);
        if (!body) return;

        EatenCount = reader.ReadPackedInt32();
        EatBody(body!);
    }

    [EventHandler(EventHandlerType.Postfix)]
    public void OnPlayerDead(PlayerMurderEvent @event)
    {
        if (!(HasArrowToBodies?.GetBool() ?? true)) return;
        if (!PlayerControl.LocalPlayer.IsRole(this)) return;

        var victim = @event.Target;
        var body = Object.FindObjectsOfType<DeadBody>().ToList().FirstOrDefault(b => b.ParentId == victim.PlayerId);
        Arrows.Add(new Arrow(body!.transform.position));
    }

    public override bool OnRoleSelection(List<CustomRole> roles)
    {
        var disableVulture = new Random().Next(2) == 1;
        CustomRole roleToDisable =
            disableVulture ? this : CustomRoleManager.GetManager().GetTypeRoleInstance<Cleaner>();
        roles.RemoveAll(r => r == roleToDisable); // 场上不能同时存在秃鹫和清洁工
        return false;
    }

    public override string HandleAdditionalPlayerName()
    {
        return $"\n({EatenCount}/{WinningEatenCount?.GetInt() ?? 4f})";
    }

    public override void ClearRoleGameData()
    {
        EatenCount = 0;
        ClosestBody = null;
        Arrows.Clear();
    }

    public override IListener GetListener()
    {
        return this;
    }

    public override CustomRole NewInstance()
    {
        return new Vulture();
    }
}