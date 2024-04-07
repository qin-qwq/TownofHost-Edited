﻿using AmongUs.GameOptions;
using TOHE.Roles.Core;
using TOHE.Roles.Neutral;
using static TOHE.MeetingHudStartPatch;
using static TOHE.Translator;

namespace TOHE.Roles.Impostor;

internal class Blackmailer : RoleBase
{
    //===========================SETUP================================\\
    private const int Id = 24600;
    private static readonly HashSet<byte> PlayerIds = [];
    public static bool HasEnabled => PlayerIds.Any();
    public override bool IsEnable => HasEnabled;
    public override CustomRoles ThisRoleBase => CustomRoles.Shapeshifter;
    //==================================================================\\

    private static OptionItem SkillCooldown;
    private static OptionItem ShowShapeshiftAnimationsOpt;

    private static readonly HashSet<byte> ForBlackmailer = [];

    public static void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Blackmailer);
        SkillCooldown = FloatOptionItem.Create(Id + 2, "BlackmailerSkillCooldown", new(2.5f, 900f, 2.5f), 20f, TabGroup.ImpostorRoles, false).SetParent(Options.CustomRoleSpawnChances[CustomRoles.Blackmailer])
           .SetValueFormat(OptionFormat.Seconds);
        ShowShapeshiftAnimationsOpt = BooleanOptionItem.Create(Id + 3, "ShowShapeshiftAnimations", true, TabGroup.ImpostorRoles, false).SetParent(Options.CustomRoleSpawnChances[CustomRoles.Blackmailer]);
    }
    public override void Init()
    {
        PlayerIds.Clear();
        ForBlackmailer.Clear();
    }
    public override void Add(byte playerId)
    {
        PlayerIds.Add(playerId);

        CustomRoleManager.MarkOthers.Add(GetMarkOthers);
    }
    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.ShapeshifterCooldown = SkillCooldown.GetFloat();
        AURoleOptions.ShapeshifterDuration = 1f;
    }
    public override bool OnCheckShapeshift(PlayerControl blackmailer, PlayerControl target, ref bool resetCooldown, ref bool shouldAnimate)
    {
        if (ShowShapeshiftAnimationsOpt.GetBool() || blackmailer.PlayerId == target.PlayerId) return true;

        DoBlackmaile(blackmailer, target);
        blackmailer.Notify(GetString("RejectShapeshift.AbilityWasUsed"), time: 2f);
        return false;
    }
    public override void OnShapeshift(PlayerControl blackmailer, PlayerControl target, bool IsAnimate, bool shapeshifting)
    {
        if (!shapeshifting) return;

        DoBlackmaile(blackmailer, target);
    }
    private static void DoBlackmaile(PlayerControl blackmailer, PlayerControl target)
    {
        if (!target.IsAlive())
        {
            blackmailer.Notify(Utils.ColorString(Utils.GetRoleColor(blackmailer.GetCustomRole()), GetString("NotAssassin")));
            return;
        }

        ClearBlackmaile();
        ForBlackmailer.Add(target.PlayerId);
    }

    public override void AfterMeetingTasks()
    {
        ClearBlackmaile();
    }
    public override void OnCoEndGame()
    {
        ClearBlackmaile();
    }

    private static void ClearBlackmaile() => ForBlackmailer.Clear();
    public static bool CheckBlackmaile(PlayerControl player) => HasEnabled && ForBlackmailer.Contains(player.PlayerId);

    private string GetMarkOthers(PlayerControl seer, PlayerControl target = null, bool isForMeeting = false)
    {
        if (!isForMeeting) return string.Empty;
        
        target ??= seer;
        return CheckBlackmaile(target) ? Utils.ColorString(Utils.GetRoleColor(CustomRoles.Blackmailer), "╳") : string.Empty;
    }
    public override void OnOthersMeetingHudStart(PlayerControl pc)
    {
        if (CheckBlackmaile(pc))
        {
            var playername = pc.GetRealName();
            if (Doppelganger.DoppelVictim.TryGetValue(pc.PlayerId, out var doppelPlayerName)) playername = doppelPlayerName;
            AddMsg(string.Format(GetString("BlackmailerDead"), playername, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Blackmailer), GetString("BlackmaileKillTitle"))));
        }
    }
}