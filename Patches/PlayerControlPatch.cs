using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using TOHE.Modules;
using TOHE.Roles.AddOns.Common;
using TOHE.Roles.AddOns.Crewmate;
using TOHE.Roles.Core.AssignManager;
using TOHE.Roles._Ghosts_.Impostor;
using TOHE.Roles._Ghosts_.Crewmate;
using TOHE.Roles.AddOns.Impostor;
using TOHE.Roles.Crewmate;
using TOHE.Roles.Double;
using TOHE.Roles.Impostor;
using TOHE.Roles.Neutral;
using TOHE.Roles.Core;
using static TOHE.Translator;

namespace TOHE;

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.OnEnable))]
class PlayerControlOnEnablePatch
{
    public static void Postfix(PlayerControl __instance)
    {
        //Shortly after this postfix, playercontrol is started but the amowner is not installed.
        //Need to delay for amowner to work
        _ = new LateTask(() =>
        {
            if (__instance.AmOwner)
            {
                Logger.Info("am owner version check, local player id is " + __instance.PlayerId, "PlayerControlOnEnable");
                RPC.RpcVersionCheck();
                return;
            }

            if (AmongUsClient.Instance.AmHost && __instance.PlayerId != PlayerControl.LocalPlayer.PlayerId)
            {
                Logger.Info("Host send version check, target player id is " + __instance.PlayerId, "PlayerControlOnEnable");
                RPC.RpcVersionCheck();
            }
        }, 0.2f, "Player Spawn LateTask ", false);

        //This late task happens where a playercontrol spawns, it will cause huge logs, so we have to hide it.
        //Its for host and joining client to recognize each other. Client and client recognize should be put in playerjoin latetask
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckProtect))]
class CheckProtectPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost || GameStates.IsHideNSeek) return false;
        Logger.Info("CheckProtect occurs: " + __instance.GetNameWithRole() + "=>" + target.GetNameWithRole(), "CheckProtect");
        var angel = __instance;
        var getAngelRole = angel.GetCustomRole();

        if (!angel.GetRoleClass().OnCheckProtect(angel, target))
            return false;

        if (angel.Is(CustomRoles.EvilSpirit))
        {
            if (target.Is(CustomRoles.Spiritcaller))
            {
                Spiritcaller.ProtectSpiritcaller();
            }
            else
            {
                Spiritcaller.HauntPlayer(target);
            }
            angel.RpcResetAbilityCooldown();
            return false;
        }

        if (angel.Is(CustomRoles.Sheriff) && angel.Data.IsDead)
        {
                Logger.Info("Blocked protection", "CheckProtect");
                return false; // What is this for? sheriff dosen't become guardian angel lmao
        }
        
        return true;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckMurder))] // Local Side Click Kill Button
class CmdCheckMurderPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        Logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}", "CmdCheckMurder");

        if (AmongUsClient.Instance.AmHost && GameStates.IsModHost)
            __instance.CheckMurder(target);
        else
        {
            MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.CheckMurder, SendOption.Reliable, -1);
            messageWriter.WriteNetObject(target);
            AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
        }

        return false;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))] // Upon Receive RPC / Local Host
class CheckMurderPatch
{
    public static Dictionary<byte, float> TimeSinceLastKill = [];
    public static void Update()
    {
        for (byte i = 0; i < 15; i++)
        {
            if (TimeSinceLastKill.ContainsKey(i))
            {
                TimeSinceLastKill[i] += Time.deltaTime;
                if (15f < TimeSinceLastKill[i]) TimeSinceLastKill.Remove(i);
            }
        }
    }
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost) return false;
        if (GameStates.IsHideNSeek) return true;

        var killer = __instance;
        var killerRole = __instance.GetCustomRole();

        Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} => {target.GetNameWithRole().RemoveHtmlTags()}", "CheckMurder");

        if (!CheckForInvalidMurdering(killer, target))
        {
            return false;
        }

        // Set kill cooldown for Chronomancer
        if (killerRole.Is(CustomRoles.Chronomancer))
            Chronomancer.OnCheckMurder(killer);

        killer.ResetKillCooldown();

        // Replacement process when the actual killer and the KILLER are different
        if (Sniper.On)
        {
            Sniper.TryGetSniper(target.PlayerId, ref killer);
            
            if (killer != __instance)
            {
                Logger.Info($"Real Killer = {killer.GetNameWithRole().RemoveHtmlTags()}", "Sniper.CheckMurder");
            }
        }

        if (CustomRoleManager.OnCheckMurder(killer, target) == false)
        {
            return false;
        }

        //== Kill target ==
        __instance.RpcMurderPlayer(target);
        //============

        return false;
    }
    public static bool CheckForInvalidMurdering(PlayerControl killer, PlayerControl target)
    {
        // Killer is already dead
        if (!killer.IsAlive())
        {
            Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} was cancelled because it is dead", "CheckMurder");
            return false;
        }

        // Is the target in a killable state?
        if (target.Data == null // Check if PlayerData is not null
            // Check target status
            || target.inVent
            || target.inMovingPlat // Moving Platform on Airhip and Zipline on Fungle
            || target.MyPhysics.Animations.IsPlayingEnterVentAnimation()
            || target.MyPhysics.Animations.IsPlayingAnyLadderAnimation()
        )
        {
            Logger.Info("The target is in an unkillable state and the kill is canceled", "CheckMurder");
            return false;
        }
        // Target Is Dead?
        if (!target.IsAlive())
        {
            Logger.Info("The target is in a dead state and the kill is canceled", "CheckMurder");
            return false;
        }
        // Checking during the meeting
        if (MeetingHud.Instance != null)
        {
            Logger.Info("In the meeting, the kill was canceled", "CheckMurder");
            return false;
        }

        var divice = Options.CurrentGameMode == CustomGameMode.FFA ? 3000f : 2000f;
        float minTime = Mathf.Max(0.02f, AmongUsClient.Instance.Ping / divice * 6f); //Ping value is milliseconds (ms), so ÷ 2000
        // No value is stored in TimeSinceLastKill || Stored time is greater than or equal to minTime => Allow kill

        //↓ If not permitted
        if (TimeSinceLastKill.TryGetValue(killer.PlayerId, out var time) && time < minTime)
        {
            Logger.Info("Kill intervals are too short and kills are canceled", "CheckMurder");
            return false;
        }
        TimeSinceLastKill[killer.PlayerId] = 0f;

        // killable decision
        if (killer.PlayerId != target.PlayerId && !killer.CanUseKillButton())
        {
            Logger.Info(killer.GetNameWithRole().RemoveHtmlTags() + " The hitter is not allowed to use the kill button and the kill is canceled", "CheckMurder");
            return false;
        }

        //FFA
        if (Options.CurrentGameMode == CustomGameMode.FFA)
        {
            FFAManager.OnPlayerAttack(killer, target);
            return true;
        }

        // if player hacked by Glitch
        if (Glitch.HasEnabled && !Glitch.OnCheckMurderOthers(killer, target))
        {
            Logger.Info("Is hacked by Glitch, it cannot kill", "Pelican.CheckMurder");
            return false;
        }

        //Is eaten player can't be killed.
        if (Pelican.IsEaten(target.PlayerId))
        {
            Logger.Info("Is eaten player can't be killed", "Pelican.CheckMurder");
            return false;
        }

        // Penguin's victim unable to kill
        if (Penguin.AbductVictim != null && killer.PlayerId == Penguin.AbductVictim.PlayerId)
        {
            killer.Notify(GetString("PenguinTargetOnCheckMurder"));
            killer.SetKillCooldown(5);
            return false;
        }

        return true;
    }

    public static bool RpcCheckAndMurder(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (!AmongUsClient.Instance.AmHost) return false;

        if (target == null) target = killer;

        // Shaman replace target
        if (Shaman.HasEnabled && Shaman.ShamanTarget != byte.MaxValue)
        {
            Logger.Info($"Real target before = {target.GetNameWithRole().RemoveHtmlTags()}", "Shaman.CheckMurder");

            target = Shaman.ChangeTarget(target);

            Logger.Info($"Real target after = {target.GetNameWithRole().RemoveHtmlTags()}", "Shaman.CheckMurder");
        }

        var killerRole = killer.GetCustomRole();
        var targetRole = target.GetCustomRole();
        var targetSubRoles = target.GetCustomSubRoles();

        var targetRoleClass = target.GetRoleClass();

        // Shield Player
        if (Main.ShieldPlayer != "" && Main.ShieldPlayer == target.GetClient().GetHashedPuid() && Utils.IsAllAlive)
        {
            Main.ShieldPlayer = "";
            killer.RpcGuardAndKill(target);
            killer.SetKillCooldown(forceAnime: true);
            return false;
        }

        // Madmate Spawn Mode Is First Kill
        if (Madmate.MadmateSpawnMode.GetInt() == 1 && Main.MadmateNum < CustomRoles.Madmate.GetCount() && target.CanBeMadmate(inGame:true))
        {
            Main.MadmateNum++;
            target.RpcSetCustomRole(CustomRoles.Madmate);
            ExtendedPlayerControl.RpcSetCustomRole(target.PlayerId, CustomRoles.Madmate);
            target.Notify(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Madmate), GetString("BecomeMadmateCuzMadmateMode")));
            killer.SetKillCooldown();
            killer.RpcGuardAndKill(target);
            target.RpcGuardAndKill(killer);
            target.RpcGuardAndKill(target);
            Logger.Info($"Madmate Spawn: {target?.Data?.PlayerName} = {target.GetCustomRole()} + {CustomRoles.Madmate}", "Assign Madmate");
            return false;
        }

        // Impostors can kill Madmate
        if (killer.Is(CustomRoleTypes.Impostor) && !Madmate.ImpCanKillMadmate.GetBool() && target.Is(CustomRoles.Madmate))
            return false;

        // Check murder on others targets
        if (!CustomRoleManager.OnCheckMurderAsTargetOnOthers(killer, target))
        {
            return false;
        }

        if (targetSubRoles.Any())
            foreach (var targetSubRole in targetSubRoles.ToArray())
            {
                switch (targetSubRole)
                {
                    case CustomRoles.Diseased:
                        Diseased.CheckMurder(killer);
                        break;

                    case CustomRoles.Antidote:
                        Antidote.CheckMurder(killer);
                        break;

                    case CustomRoles.Susceptible:
                        Susceptible.CallEnabledAndChange(target);
                        if (Main.PlayerStates[target.PlayerId].deathReason == PlayerState.DeathReason.Vote)
                            Main.PlayerStates[target.PlayerId].deathReason = PlayerState.DeathReason.Kill; // When susceptible is still alive "Vote" triggers role visibility for others.
                        break;

                    case CustomRoles.Fragile:
                        if (Fragile.KillFragile(killer, target))
                            return false;
                        break;

                    case CustomRoles.Aware:
                        Aware.OnCheckMurder(killerRole, target);
                        break;

                    case CustomRoles.Lucky:
                        if (!Lucky.OnCheckMurder(killer, target))
                            return false;
                        break;

                    case CustomRoles.Cyber when killer.PlayerId != target.PlayerId:
                        foreach (var pc in Main.AllAlivePlayerControls.Where(x => x.PlayerId != target.PlayerId).ToArray())
                        {
                            if (target.Is(CustomRoles.Cyber))
                            {
                                if (Main.AllAlivePlayerControls.Any(x =>
                                    x.PlayerId != killer.PlayerId &&
                                    x.PlayerId != target.PlayerId &&
                                    Vector2.Distance(x.transform.position, target.transform.position) < 2f))
                                    return false;
                            }
                        }
                        break;
                }
            }

        // Check Murder as target
        if (!targetRoleClass.OnCheckMurderAsTarget(killer, target))
        {
            return false;
        }

        if (!check) killer.RpcMurderPlayer(target);
        return true;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
class MurderPlayerPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] MurderResultFlags resultFlags, ref bool __state)
    {
        Logger.Info($"{__instance.GetNameWithRole().RemoveHtmlTags()} => {target.GetNameWithRole().RemoveHtmlTags()}{(target.IsProtected() ? "(Protected)" : "")}, flags : {resultFlags}", "MurderPlayer Prefix");

        var isProtectedByClient = resultFlags.HasFlag(MurderResultFlags.DecisionByHost) && target.IsProtected();
        var isProtectedByHost = resultFlags.HasFlag(MurderResultFlags.FailedProtected);
        var isFailed = resultFlags.HasFlag(MurderResultFlags.FailedError);
        var isSucceeded = __state = !isProtectedByClient && !isProtectedByHost && !isFailed;
        
        if (isProtectedByClient)
        {
            Logger.Info("The kill will fail because it has DecisonByHost and target is protected", "MurderPlayer Prefix");
        }
        if (isProtectedByHost)
        {
            if (GameStates.IsModHost)
                Logger.Info("Host sent FailedProtected due to role skill / reset kill timer", "MurderPlayer Prefix");
            else
                Logger.Info("Vanilla server canceled murder due to protection", "MurderPlayer Prefix");
        }
        if (isFailed)
        {
            if (GameStates.IsModHost)
                Logger.Info("The kill was cancelled by the host", "MurderPlayer Prefix");
            else
                Logger.Info("The kill was cancelled by the server", "MurderPlayer Prefix");
        }

        if (isSucceeded && AmongUsClient.Instance.AmHost)
        {
            if (target.shapeshifting)
            {
                // During shapeshift animation
                // Delay 1s to account for animation time, plus +0.5s to account for lag with the client
                _ = new LateTask(
                    () =>
                    {
                        if (GameStates.IsInTask)
                        {
                            target.RpcShapeshift(target, false);
                        }
                    },
                    1.5f, "Revert Shapeshift Before Murder");
            }
            else
            {
                if (Main.CheckShapeshift.TryGetValue(target.PlayerId, out var shapeshifting) && shapeshifting)
                {
                    //Shapeshift revert
                    target.RpcShapeshift(target, false);
                }
            }

            if (!target.IsProtected() && !Doppelganger.CheckDoppelVictim(target.PlayerId) && !Camouflage.ResetSkinAfterDeathPlayers.Contains(target.PlayerId))
            {
                Camouflage.ResetSkinAfterDeathPlayers.Add(target.PlayerId);
                Camouflage.RpcSetSkin(target, ForceRevert: true, RevertToDefault: true);
            }
        }


        return true;
    }
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target/*, [HarmonyArgument(1)] MurderResultFlags resultFlags*/, bool __state)
    {
        if (!__state)
        {
            return;
        }
        if (GameStates.IsHideNSeek) return;
        if (target.AmOwner) RemoveDisableDevicesPatch.UpdateDisableDevices();
        if (!target.Data.IsDead || !AmongUsClient.Instance.AmHost) return;

        if (Main.OverDeadPlayerList.Contains(target.PlayerId)) return;

        PlayerControl killer = __instance;

        if (killer != __instance)
        {
            Logger.Info($"Real Killer => {killer.GetNameWithRole().RemoveHtmlTags()}", "MurderPlayer");

        }
        if (Main.PlayerStates[target.PlayerId].deathReason == PlayerState.DeathReason.etc)
        {
            Main.PlayerStates[target.PlayerId].deathReason = PlayerState.DeathReason.Kill;
        }

        // Check Youtuber first died
        if (Main.FirstDied == "" && target.Is(CustomRoles.Youtuber) && !killer.Is(CustomRoles.KillingMachine))
        {
            CustomSoundsManager.RPCPlayCustomSoundAll("Congrats");
            if (!CustomWinnerHolder.CheckForConvertedWinner(target.PlayerId))
            {
                CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Youtuber);
                CustomWinnerHolder.WinnerIds.Add(target.PlayerId);
            }
            return;
            //Imagine youtuber is converted
        }
        if (Main.FirstDied == "")
            Main.FirstDied = target.GetClient().GetHashedPuid();

        if (Main.AllKillers.ContainsKey(killer.PlayerId))
            Main.AllKillers.Remove(killer.PlayerId);

        if (!killer.Is(CustomRoles.Trickster))
            Main.AllKillers.Add(killer.PlayerId, Utils.GetTimeStamp());

        AfterPlayerDeathTasks(killer, target, false);

        Main.PlayerStates[target.PlayerId].SetDead();
        target.SetRealKiller(killer, true);
        Utils.CountAlivePlayers(true);

        Utils.TargetDies(__instance, target);

        if (Options.LowLoadMode.GetBool())
        {
            __instance.MarkDirtySettings();
            target.MarkDirtySettings();
        }
        else
        {
            Utils.SyncAllSettings();
        }

        Utils.NotifyRoles(SpecifySeer: killer);
        Utils.NotifyRoles(SpecifySeer: target);
    }
    public static void AfterPlayerDeathTasks(PlayerControl killer, PlayerControl target, bool inMeeting)
    {
        CustomRoleManager.OnMurderPlayer(killer, target, inMeeting);
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcMurderPlayer))]
class RpcMurderPlayerPatch
{
    public static bool Prefix(PlayerControl __instance, PlayerControl target, bool didSucceed)
    {
        if (!AmongUsClient.Instance.AmHost)
            Logger.Error("Client is calling RpcMurderPlayer, are you Hacking?", "RpcMurderPlayerPatch..Prefix");

        MurderResultFlags murderResultFlags = didSucceed ? MurderResultFlags.Succeeded : MurderResultFlags.FailedError;
        if (AmongUsClient.Instance.AmClient)
        {
            __instance.MurderPlayer(target, murderResultFlags);
        }
        MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.MurderPlayer, SendOption.Reliable, -1);
        messageWriter.WriteNetObject(target);
        messageWriter.Write((int)murderResultFlags);
        AmongUsClient.Instance.FinishRpcImmediately(messageWriter);

        return false;
        // There is no need to include DecisionByHost. DecisionByHost will make client check protection locally and cause confusion.
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift))]
public static class CheckShapeShiftPatch
{
    public static void RejectShapeshiftAndReset(this PlayerControl player, bool reset = true)
    {
        player.RpcRejectShapeshift();
        if (reset) player.RpcResetAbilityCooldown();
        Logger.Info($"Rejected {player.GetRealName()} shapeshift & " + (reset ? "Reset cooldown" : "Not Reset cooldown"), "RejectShapeshiftAndReset");
    }
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] bool shouldAnimate)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsModHost) return true;
        if (__instance.PlayerId == target.PlayerId) return true;
        if (!Options.DisableShapeshiftAnimations.GetBool()) return true;

        var shapeshifter = __instance;
        var role = shapeshifter.GetCustomRole();

        // Always show
        if (role is CustomRoles.ShapeshifterTOHE or CustomRoles.Shapeshifter or CustomRoles.ShapeMaster or CustomRoles.Hangman or CustomRoles.Morphling or CustomRoles.Glitch) return true;

        // Check Sniper settings conditions
        if (role is CustomRoles.Sniper && Sniper.ShowShapeshiftAnimations) return true;

        Logger.Info($"{shapeshifter.GetRealName()} => {target.GetRealName()}, shouldAnimate = {shouldAnimate}", "Check ShapeShift");

        if (role.GetVNRole() != CustomRoles.Shapeshifter)
        {
            shapeshifter.RejectShapeshiftAndReset();
            Logger.Info($"Rejected bcz {shapeshifter.GetRealName()} is not shapeshifter in mod roles", "Check ShapeShift");
            return false;
        }

        if (Pelican.IsEaten(shapeshifter.PlayerId))
        {
            shapeshifter.RejectShapeshiftAndReset();
            Logger.Info($"Rejected bcz {shapeshifter.GetRealName()} is eaten by Pelican", "Check ShapeShift");
            return false;
        }

        if (!shapeshifter.IsAlive())
        {
            shapeshifter.RejectShapeshiftAndReset();
            Logger.Info($"Rejected bcz {shapeshifter.GetRealName()} is dead", "Check ShapeShift");
            return false;
        }

        bool shapeshiftIsHidden = true;
        shapeshifter.RejectShapeshiftAndReset();
        shapeshifter.GetRoleClass()?.OnShapeshift(shapeshifter, target, false, shapeshiftIsHidden);
        return false;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Shapeshift))]
class ShapeshiftPatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        var shapeshifter = __instance;

        if (Options.DisableShapeshiftAnimations.GetBool())
        {
            var role = shapeshifter.GetCustomRole();

            Logger.Info($"shapeshifter {__instance?.GetRealName()}:role: {role} => {target?.GetNameWithRole().RemoveHtmlTags()}", "ShapeshiftPatch.DisableShapeshiftAnimations");

            // Check shapeshift
            if (!(
                (role is CustomRoles.ShapeshifterTOHE or CustomRoles.Shapeshifter or CustomRoles.ShapeMaster or CustomRoles.Hangman or CustomRoles.Morphling or CustomRoles.Glitch)
                ||
                (role is CustomRoles.Sniper && Sniper.ShowShapeshiftAnimations)
                ))
                return;
        }

        Logger.Info($"{__instance?.GetNameWithRole().RemoveHtmlTags()} => {target?.GetNameWithRole().RemoveHtmlTags()}", "ShapeshiftPatch");

        var shapeshifting = shapeshifter.PlayerId != target.PlayerId;

        if (Main.CheckShapeshift.TryGetValue(shapeshifter.PlayerId, out var last) && last == shapeshifting)
        {
            Logger.Info($"{__instance?.GetNameWithRole().RemoveHtmlTags()} : Cancel Shapeshift.Prefix", "Shapeshift");
            return;
        }

        Main.CheckShapeshift[shapeshifter.PlayerId] = shapeshifting;
        Main.ShapeshiftTarget[shapeshifter.PlayerId] = target.PlayerId;

        if (!AmongUsClient.Instance.AmHost) return;
        if (GameStates.IsHideNSeek) return;
        if (!shapeshifting) Camouflage.RpcSetSkin(__instance);

        if (!Pelican.IsEaten(shapeshifter.PlayerId))
        {
            var shapeshiftIsHidden = false;
            shapeshifter.GetRoleClass()?.OnShapeshift(shapeshifter, target, shapeshifting, shapeshiftIsHidden);
        }

        //変身解除のタイミングがずれて名前が直せなかった時のために強制書き換え
        if (!shapeshifting && !shapeshifter.Is(CustomRoles.Glitch))
        {
            _ = new LateTask(() =>
            {
                Utils.NotifyRoles(NoCache: true);
            },
            1.2f, "ShapeShiftNotify");
        }
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
class ReportDeadBodyPatch
{
    public static Dictionary<byte, bool> CanReport;
    public static Dictionary<byte, List<GameData.PlayerInfo>> WaitReport = [];
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] GameData.PlayerInfo target)
    {
        if (GameStates.IsMeeting || GameStates.IsHideNSeek) return false;

        if (EAC.RpcReportDeadBodyCheck(__instance, target))
        {
            Logger.Fatal("Eac patched the report body rpc", "ReportDeadBodyPatch");
            return false;
        }
        if (Options.DisableMeeting.GetBool()) return false;
        if (Options.CurrentGameMode == CustomGameMode.FFA) return false;

        if (!CanReport[__instance.PlayerId])
        {
            WaitReport[__instance.PlayerId].Add(target);
            Logger.Warn($"{__instance.GetNameWithRole().RemoveHtmlTags()} : Reporting is prohibited and will wait until it becomes possible", "ReportDeadBody");
            return false;
        }

        Logger.Info($"{__instance.GetNameWithRole().RemoveHtmlTags()} => {target?.Object?.GetNameWithRole().RemoveHtmlTags() ?? "null"}", "ReportDeadBody");

        foreach (var kvp in Main.PlayerStates)
        {
            var pc = Utils.GetPlayerById(kvp.Key);
            kvp.Value.LastRoom = pc.GetPlainShipRoom();
        }

        if (!AmongUsClient.Instance.AmHost) return true;

        try
        {
            // If the player is dead, the meeting is canceled
            if (__instance.Data.IsDead) return false;

            //=============================================
            //Below, check if this meeting is allowed
            //=============================================

            var killer = target?.Object?.GetRealKiller();
            var killerRole = killer?.GetCustomRole();

            if (target == null) //Meeting
            {
                var playerRoleClass = __instance.GetRoleClass();

                if (playerRoleClass.OnCheckStartMeeting(__instance) == false)
                {
                    Logger.Info($"Player has role class: {playerRoleClass} - the start of the meeting has been cancelled", "ReportDeadBody");
                    return false;
                }
            }
            if (target != null) // Report dead body
            {
                // Guessed player cannot report
                if (Main.PlayerStates[target.PlayerId].deathReason == PlayerState.DeathReason.Gambled) return false;

                // Check report bead body
                foreach (var player in Main.PlayerStates.Values.ToArray())
                {
                    var playerRoleClass = player.RoleClass;
                    if (player == null ||  playerRoleClass == null) continue;

                    if (playerRoleClass.OnCheckReportDeadBody(__instance, target, killer) == false)
                    {
                        Logger.Info($"Player has role class: {playerRoleClass} - is canceled the report", "ReportDeadBody");
                        return false;
                    }
                }

                // if Bait is killed, check the setting condition
                if (!(target.Object.Is(CustomRoles.Bait) && Bait.BaitCanBeReportedUnderAllConditions.GetBool()))
                {
                    // Comms Camouflage
                    if (Options.DisableReportWhenCC.GetBool() && Utils.IsActive(SystemTypes.Comms) && Camouflage.IsActive) return false;
                }

                //Check unreportable bodies
                if (Main.UnreportableBodies.Contains(target.PlayerId))
                {
                    __instance.Notify(Utils.ColorString(__instance.GetRoleColor(), GetString("BodyCannotBeReported")));
                    return false;
                }

                if (target.Object.Is(CustomRoles.Unreportable)) return false;


                // 胆小鬼不敢报告
                var tpc = Utils.GetPlayerById(target.PlayerId);
                if (__instance.Is(CustomRoles.Oblivious))
                {
                    if (!tpc.Is(CustomRoles.Bait) || (tpc.Is(CustomRoles.Bait) && Oblivious.ObliviousBaitImmune.GetBool())) /* && (target?.Object != null)*/
                    {
                        return false;
                    }
                }

                var tar = Utils.GetPlayerById(target.PlayerId);

                if (__instance.Is(CustomRoles.Unlucky) && (target?.Object == null || !target.Object.Is(CustomRoles.Bait)))
                {
                    var Ue = IRandom.Instance;
                    if (Ue.Next(1, 100) <= Unlucky.UnluckyReportSuicideChance.GetInt())
                    {
                        Main.PlayerStates[__instance.PlayerId].deathReason = PlayerState.DeathReason.Suicide;
                        __instance.RpcMurderPlayer(__instance);
                        return false;
                    }
                }
            }

            if (Options.SyncButtonMode.GetBool() && target == null)
            {
                Logger.Info($"Option: {Options.SyncedButtonCount.GetInt()}, has button count: {Options.UsedButtonCount}", "ReportDeadBody");
                if (Options.SyncedButtonCount.GetFloat() <= Options.UsedButtonCount)
                {
                    Logger.Info("The button has been canceled because the maximum number of available buttons has been exceeded", "ReportDeadBody");
                    return false;
                }
                else Options.UsedButtonCount++;
                
                if (Options.SyncedButtonCount.GetFloat() == Options.UsedButtonCount)
                {
                    Logger.Info("The maximum number of meeting buttons has been reached", "ReportDeadBody");
                }
            }

            AfterReportTasks(__instance, target);

        }
        catch (Exception e)
        {
            Logger.Exception(e, "ReportDeadBodyPatch");
            Logger.SendInGame("Error: " + e.ToString());

            // If there is an error in ReportDeadBodyPatch, update the player nicknames anyway
            MeetingTimeManager.OnReportDeadBody();
            NameNotifyManager.Reset();
            Utils.DoNotifyRoles(isForMeeting: true, NoCache: true, CamouflageIsForMeeting: true);
            _ = new LateTask(Utils.SyncAllSettings, 3f, "Sync all settings after report");
        }

        return true;
    }
    public static void AfterReportTasks(PlayerControl player, GameData.PlayerInfo target)
    {
        //=============================================
        // Hereinafter, it is assumed that the button is confirmed to be pressed
        //=============================================

        Main.LastVotedPlayerInfo = null;
        Main.GuesserGuessed.Clear();
        Main.AllKillers.Clear();


        foreach (var playerStates in Main.PlayerStates.Values.ToArray())
        {
            playerStates.RoleClass?.OnReportDeadBody(player, target?.Object);
        }

        // Alchemist & Bloodlust
        Alchemist.OnReportDeadBodyGlobal();

        if (Aware.IsEnable) Aware.OnReportDeadBody();
        
        Sleuth.OnReportDeadBody(player, target?.Object);



        foreach (var pc in Main.AllPlayerControls)
        {
            if (!Doppelganger.CheckDoppelVictim(pc.PlayerId))
            {
                // Update skins again, since players have different skins
                // And can be easily distinguished from each other
                if (Camouflage.IsCamouflage && Options.KPDCamouflageMode.GetValue() is 2 or 3)
                {
                    Camouflage.RpcSetSkin(pc);
                }

                // Check shapeshift and revert skin to default
                if (Main.CheckShapeshift.ContainsKey(pc.PlayerId))
                {
                    Camouflage.RpcSetSkin(pc, RevertToDefault: true);
                }
            }

            Logger.Info($"Player {pc?.Data?.PlayerName}: Id {pc.PlayerId} - is alive: {pc.IsAlive()}", "CheckIsAlive");
        }

        // Set meeting time
        MeetingTimeManager.OnReportDeadBody();

        // Clear all Notice players
        NameNotifyManager.Reset();

        // Update Notify Roles for Meeting
        Utils.DoNotifyRoles(isForMeeting: true, NoCache: true, CamouflageIsForMeeting: true);

        // Sync all settings on meeting start
        _ = new LateTask(Utils.SyncAllSettings, 3f, "Sync all settings after report");
    }
    public static async void ChangeLocalNameAndRevert(string name, int time)
    {
        //async Taskじゃ警告出るから仕方ないよね。
        var revertName = PlayerControl.LocalPlayer.name;
        PlayerControl.LocalPlayer.RpcSetNameEx(name);
        await Task.Delay(time);
        PlayerControl.LocalPlayer.RpcSetNameEx(revertName);
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
class FixedUpdateInNormalGamePatch
{
    private static readonly StringBuilder Mark = new(20);
    private static readonly StringBuilder Suffix = new(120);
    private static readonly Dictionary<int, int> BufferTime = [];
    private static int LevelKickBufferTime = 20;

    public static async void Postfix(PlayerControl __instance)
    {
        if (GameStates.IsHideNSeek) return;
        if (!GameStates.IsModHost) return;
        if (__instance == null) return;

        byte id = __instance.PlayerId;
        if (AmongUsClient.Instance.AmHost && GameStates.IsInTask && ReportDeadBodyPatch.CanReport[id] && ReportDeadBodyPatch.WaitReport[id].Count > 0)
        {
            if(!Glitch.OnCheckFixedUpdateReport(__instance, id))
            { }
            else
            {
                var info = ReportDeadBodyPatch.WaitReport[id][0];
                ReportDeadBodyPatch.WaitReport[id].Clear();
                Logger.Info($"{__instance.GetNameWithRole().RemoveHtmlTags()}: The report will be processed now that it is available for reporting", "ReportDeadbody");
                __instance.ReportDeadBody(info);
            }
        }

        try
        {
            await DoPostfix(__instance);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error for {__instance.GetNameWithRole().RemoveHtmlTags()}:  {ex}", "FixedUpdateInNormalGamePatch");
        }
    }

    public static Task DoPostfix(PlayerControl __instance)
    {
        var player = __instance;

        bool lowLoad = false;
        if (Options.LowLoadMode.GetBool())
        {
            if (!BufferTime.TryGetValue(player.PlayerId, out var timerLowLoad))
            {
                BufferTime.TryAdd(player.PlayerId, 10);
                timerLowLoad = 10;
            }

            timerLowLoad--;

            if (timerLowLoad > 0)
            {
                lowLoad = true;
            }
            else
            {
                timerLowLoad = 10;
            }

            BufferTime[player.PlayerId] = timerLowLoad;
        }

        if (!lowLoad)
        {
            Zoom.OnFixedUpdate();
        }

        // Only during the game
        if (GameStates.IsInGame)
        {
            Sniper.OnFixedUpdateGlobal(player);

            if (!lowLoad)
            {
                NameNotifyManager.OnFixedUpdate(player);
                TargetArrow.OnFixedUpdate(player);
                LocateArrow.OnFixedUpdate(player);
            }
        }

        if (AmongUsClient.Instance.AmHost)
        {
            if (GameStates.IsLobby)
            {
                bool shouldChangeGamePublic = (ModUpdater.hasUpdate && ModUpdater.forceUpdate) || ModUpdater.isBroken || !Main.AllowPublicRoom || !VersionChecker.IsSupported;
                if (shouldChangeGamePublic && AmongUsClient.Instance.IsGamePublic)
                {
                    AmongUsClient.Instance.ChangeGamePublic(false);
                }

                bool playerInAllowList = false;
                if (Options.ApplyAllowList.GetBool())
                {
                    playerInAllowList = BanManager.CheckAllowList(player.Data.FriendCode);
                }

                if (!playerInAllowList)
                {
                    bool shouldKickLowLevelPlayer = !lowLoad && !player.AmOwner && Options.KickLowLevelPlayer.GetInt() != 0 && player.Data.PlayerLevel != 0 && player.Data.PlayerLevel < Options.KickLowLevelPlayer.GetInt();

                    if (shouldKickLowLevelPlayer)
                    {
                        LevelKickBufferTime--;

                        if (LevelKickBufferTime <= 0)
                        {
                            LevelKickBufferTime = 20;
                            if (!Options.TempBanLowLevelPlayer.GetBool())
                            {
                                AmongUsClient.Instance.KickPlayer(player.GetClientId(), false);
                                string msg = string.Format(GetString("KickBecauseLowLevel"), player.GetRealName().RemoveHtmlTags());
                                Logger.SendInGame(msg);
                                Logger.Info(msg, "Low Level Kick");
                            }
                            else
                            {
                                if (player.GetClient().ProductUserId != "")
                                {
                                    if (!BanManager.TempBanWhiteList.Contains(player.GetClient().GetHashedPuid()))
                                        BanManager.TempBanWhiteList.Add(player.GetClient().GetHashedPuid());
                                }
                                string msg = string.Format(GetString("TempBannedBecauseLowLevel"), player.GetRealName().RemoveHtmlTags());
                                Logger.SendInGame(msg);
                                AmongUsClient.Instance.KickPlayer(player.GetClientId(), true);
                                Logger.Info(msg, "Low Level Temp Ban");
                            }
                        }
                    }
                }

                if (KickPlayerPatch.AttemptedKickPlayerList.Count > 0)
                {
                    foreach (var item in KickPlayerPatch.AttemptedKickPlayerList)
                    {
                        KickPlayerPatch.AttemptedKickPlayerList[item.Key]++;

                        if (item.Value > 11)
                            KickPlayerPatch.AttemptedKickPlayerList.Remove(item.Key);
                    }
                }
            }

            DoubleTrigger.OnFixedUpdate(player);

            //Mini's count down needs to be done outside if intask if we are counting meeting time
            if (GameStates.IsInGame && player.Is(CustomRoles.NiceMini) || player.Is(CustomRoles.EvilMini))
            {
                if (!player.Data.IsDead)
                    Mini.OnFixedUpdates(player);
            }

            if (GameStates.IsInTask)
            {
                CustomRoleManager.OnFixedUpdate(player);

                if (player.Is(CustomRoles.Statue) && player.IsAlive())
                    Statue.OnFixedUpdate(player);
            
                if (!lowLoad)
                {
                    CustomRoleManager.OnFixedUpdateLowLoad(player);

                    if (Rainbow.isEnabled)
                        Rainbow.OnFixedUpdate();

                    if (Options.LadderDeath.GetBool() && player.IsAlive())
                        FallFromLadder.FixedUpdate(player);

                    if (GameStates.IsInGame && CustomRoles.Lovers.IsEnable())
                        LoversSuicide();


                    //Local Player only
                    if (player.AmOwner)
                    {
                        DisableDevice.FixedUpdate();
                    }
                }
            }

            if (!lowLoad)
            {
                if (!Main.DoBlockNameChange)
                    Utils.ApplySuffix(__instance);

                if (GameStates.IsInGame && Main.RefixCooldownDelay <= 0)
                    foreach (var pc in Main.AllPlayerControls)
                    {
                        if (pc.Is(CustomRoles.Vampire) || pc.Is(CustomRoles.Warlock) || pc.Is(CustomRoles.Ninja) || pc.Is(CustomRoles.Vampiress))
                            Main.AllPlayerKillCooldown[pc.PlayerId] = Options.DefaultKillCooldown * 2;
                        
                        if (pc.Is(CustomRoles.Poisoner))
                            Main.AllPlayerKillCooldown[pc.PlayerId] = Poisoner.KillCooldown.GetFloat() * 2;
                    }
            }
        }

        //Local Player only
        if (player.AmOwner && GameStates.IsInTask)
        {
            //Kill target override processing
            if (!player.Is(CustomRoleTypes.Impostor) && player.CanUseKillButton() && !player.Data.IsDead)
            {
                var players = __instance.GetPlayersInAbilityRangeSorted(false);
                PlayerControl closest = players.Count <= 0 ? null : players[0];
                HudManager.Instance.KillButton.SetTarget(closest);
            }
        }

        var RoleTextTransform = __instance.cosmetics.nameText.transform.Find("RoleText");
        var RoleText = RoleTextTransform.GetComponent<TMPro.TextMeshPro>();

        if (RoleText != null && __instance != null && !lowLoad)
        {
            if (GameStates.IsLobby)
            {
                if (Main.playerVersion.TryGetValue(__instance.GetClientId(), out var ver))
                {
                    if (Main.ForkId != ver.forkId) // フォークIDが違う場合
                        __instance.cosmetics.nameText.text = $"<color=#ff0000><size=1.2>{ver.forkId}</size>\n{__instance?.name}</color>";
                    else if (Main.version.CompareTo(ver.version) == 0)
                        __instance.cosmetics.nameText.text = ver.tag == $"{ThisAssembly.Git.Commit}({ThisAssembly.Git.Branch})" ? $"<color=#87cefa>{__instance.name}</color>" : $"<color=#ffff00><size=1.2>{ver.tag}</size>\n{__instance?.name}</color>";
                    else __instance.cosmetics.nameText.text = $"<color=#ff0000><size=1.2>v{ver.version}</size>\n{__instance?.name}</color>";
                }
                else __instance.cosmetics.nameText.text = __instance?.Data?.PlayerName;
            }
            if (GameStates.IsInGame)
            {
                var RoleTextData = Utils.GetRoleAndSubText(PlayerControl.LocalPlayer.PlayerId, __instance.PlayerId);
                RoleText.text = RoleTextData.Item1;
                RoleText.color = RoleTextData.Item2;
                if (Options.CurrentGameMode == CustomGameMode.FFA) RoleText.text = string.Empty;
                
                if (__instance.AmOwner || Options.CurrentGameMode == CustomGameMode.FFA) RoleText.enabled = true;
                else if (ExtendedPlayerControl.KnowRoleTarget(PlayerControl.LocalPlayer, __instance)) RoleText.enabled = true;
                else RoleText.enabled = false;
                
                if (!PlayerControl.LocalPlayer.Data.IsDead && Overseer.IsRevealedPlayer(PlayerControl.LocalPlayer, __instance) && __instance.Is(CustomRoles.Trickster))
                {
                    RoleText.text = Overseer.GetRandomRole(PlayerControl.LocalPlayer.PlayerId); // random role for revealed trickster
                    RoleText.text += TaskState.GetTaskState(); // random task count for revealed trickster
                }

                if (!AmongUsClient.Instance.IsGameStarted && AmongUsClient.Instance.NetworkMode != NetworkModes.FreePlay)
                {
                    RoleText.enabled = false;
                    if (!__instance.AmOwner) __instance.cosmetics.nameText.text = __instance?.Data?.PlayerName;
                }

                if (Main.VisibleTasksCount)
                    RoleText.text += Utils.GetProgressText(__instance);


                var seer = PlayerControl.LocalPlayer;
                var seerRoleClass = seer.GetRoleClass();
                var target = __instance;

                string RealName = target.GetRealName();

                Mark.Clear();
                Suffix.Clear();


                if (target.AmOwner && GameStates.IsInTask)
                {
                    if (Options.CurrentGameMode == CustomGameMode.FFA)
                        FFAManager.GetNameNotify(target, ref RealName);

                    if (Pelican.IsEaten(seer.PlayerId))
                        RealName = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Pelican), GetString("EatenByPelican"));

                    if (Deathpact.IsInActiveDeathpact(seer))
                        RealName = Deathpact.GetDeathpactString(seer);

                    if (NameNotifyManager.GetNameNotify(target, out var name))
                        RealName = name;
                }

                RealName = RealName.ApplyNameColorData(seer, target, false);
                var seerRole = seer.GetCustomRole();


                Mark.Append(seerRoleClass?.GetMark(seer, target, false));
                Mark.Append(CustomRoleManager.GetMarkOthers(seer, target, false));

                Suffix.Append(CustomRoleManager.GetLowerTextOthers(seer, target));

                Suffix.Append(seerRoleClass?.GetSuffix(seer, target));
                Suffix.Append(CustomRoleManager.GetSuffixOthers(seer, target));

                if (seerRole.IsImpostor() && target.GetPlayerTaskState().IsTaskFinished)
                {
                    if (target.Is(CustomRoles.Snitch) && target.Is(CustomRoles.Madmate))
                        Mark.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), "★"));
                }

                if (target.Is(CustomRoles.Cyber) && Cyber.CyberKnown.GetBool())
                    Mark.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Cyber), "★"));

                if (target.Is(CustomRoles.Lovers) && seer.Is(CustomRoles.Lovers))
                {
                    Mark.Append($"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♥</color>");
                }
                else if (target.Is(CustomRoles.Lovers) && seer.Data.IsDead)
                {
                    Mark.Append($"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♥</color>");
                }
                else if (target.Is(CustomRoles.Ntr) || seer.Is(CustomRoles.Ntr))
                {
                    Mark.Append($"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♥</color>");
                }
                else if (target == seer && CustomRolesHelper.RoleExist(CustomRoles.Ntr))
                {
                    Mark.Append($"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♥</color>");
                }


                if (Options.CurrentGameMode == CustomGameMode.FFA)
                    Suffix.Append(FFAManager.GetPlayerArrow(seer, target));

                /*if(main.AmDebugger.Value && main.BlockKilling.TryGetValue(target.PlayerId, out var isBlocked)) {
                    Mark = isBlocked ? "(true)" : "(false)";}*/

                // Devourer
                if (CustomRoles.Devourer.HasEnabled())
                {
                    bool targetDevoured = Devourer.HideNameOfTheDevoured(target.PlayerId);
                    if (targetDevoured)
                        RealName = GetString("DevouredName");
                }

                // Camouflage
                if ((Utils.IsActive(SystemTypes.Comms) && Camouflage.IsActive) || Camouflager.AbilityActivated)
                    RealName = $"<size=0%>{RealName}</size> ";

                string DeathReason = seer.Data.IsDead && seer.KnowDeathReason(target)
                    ? $" ({Utils.ColorString(Utils.GetRoleColor(CustomRoles.Doctor), Utils.GetVitalText(target.PlayerId))})" : string.Empty;

                target.cosmetics.nameText.text = $"{RealName}{DeathReason}{Mark}";

                if (Suffix.ToString() != "")
                {
                    RoleText.transform.SetLocalY(0.35f);
                    target.cosmetics.nameText.text += "\r\n" + Suffix.ToString();
                }
                else
                {
                    RoleText.transform.SetLocalY(0.2f);
                }
            }
            else
            {
                RoleText.transform.SetLocalY(0.2f);
            }
        }
        return Task.CompletedTask;
    }
    //FIXME: 役職クラス化のタイミングで、このメソッドは移動予定
    public static void LoversSuicide(byte deathId = 0x7f, bool isExiled = false)
    {
        if (Options.LoverSuicide.GetBool() && Main.isLoversDead == false)
        {
            foreach (var loversPlayer in Main.LoversPlayers.ToArray())
            {
                //生きていて死ぬ予定でなければスキップ
                if (!loversPlayer.Data.IsDead && loversPlayer.PlayerId != deathId) continue;

                Main.isLoversDead = true;
                foreach (var partnerPlayer in Main.LoversPlayers.ToArray())
                {
                    //本人ならスキップ
                    if (loversPlayer.PlayerId == partnerPlayer.PlayerId) continue;

                    //残った恋人を全て殺す(2人以上可)
                    //生きていて死ぬ予定もない場合は心中
                    if (partnerPlayer.PlayerId != deathId && !partnerPlayer.Data.IsDead)
                    {
                        if (partnerPlayer.Is(CustomRoles.Lovers))
                        {
                            Main.PlayerStates[partnerPlayer.PlayerId].deathReason = PlayerState.DeathReason.FollowingSuicide;
                            if (isExiled)
                                CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.FollowingSuicide, partnerPlayer.PlayerId);
                            else
                                partnerPlayer.RpcMurderPlayer(partnerPlayer);
                        }
                    }
                }
            }
        }
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start))]
class PlayerStartPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        if (GameStates.IsHideNSeek) return;

        var roleText = UnityEngine.Object.Instantiate(__instance.cosmetics.nameText);
        roleText.transform.SetParent(__instance.cosmetics.nameText.transform);
        roleText.transform.localPosition = new Vector3(0f, 0.2f, 0f);
        roleText.fontSize -= 1.2f;
        roleText.text = "RoleText";
        roleText.gameObject.name = "RoleText";
        roleText.enabled = false;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetColor))]
class SetColorPatch
{
    public static bool IsAntiGlitchDisabled = false;
    public static bool Prefix(PlayerControl __instance, int bodyColor)
    {
        //色変更バグ対策
        if (!AmongUsClient.Instance.AmHost || __instance.CurrentOutfit.ColorId == bodyColor || IsAntiGlitchDisabled) return true;
        return true;
    }
}

[HarmonyPatch(typeof(Vent), nameof(Vent.EnterVent))]
class EnterVentPatch
{
    public static void Postfix(Vent __instance, [HarmonyArgument(0)] PlayerControl pc)
    {
        if (GameStates.IsHideNSeek) return;

        Main.LastEnteredVent.Remove(pc.PlayerId);
        Main.LastEnteredVent.Add(pc.PlayerId, __instance);
        Main.LastEnteredVentLocation.Remove(pc.PlayerId);
        Main.LastEnteredVentLocation.Add(pc.PlayerId, pc.GetCustomPosition());

        if (!AmongUsClient.Instance.AmHost) return;

        pc.GetRoleClass()?.OnEnterVent(pc, __instance);

        if (pc.Is(CustomRoles.Unlucky))
        {
            Unlucky.SuicideRand(pc);
        }
    }
}
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoEnterVent))]
class CoEnterVentPatch
{
    public static bool Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] int id)
    {
        if (!AmongUsClient.Instance.AmHost || GameStates.IsHideNSeek) return true;
        Logger.Info($" {__instance.myPlayer.GetNameWithRole().RemoveHtmlTags()}, Vent ID: {id}", "CoEnterVent");

        //FFA
        if (Options.CurrentGameMode == CustomGameMode.FFA && FFAManager.CheckCoEnterVent(__instance, id))
        {
            return true;
        }

        // Check others enter to vent
        if (CustomRoleManager.OthersCoEnterVent(__instance, id))
        {
            return true;
        }

        var playerRoleClass = __instance.myPlayer.GetRoleClass();
        
        // Fix Vent Stuck
        if ((__instance.myPlayer.Data.Role.Role != RoleTypes.Engineer && !__instance.myPlayer.CanUseImpostorVentButton())
            || (playerRoleClass != null && playerRoleClass.CheckBootFromVent(__instance, id))
        )
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, -1);
            writer.WritePacked(127);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            
            _ = new LateTask(() =>
            {
                int clientId = __instance.myPlayer.GetClientId();
                MessageWriter writer2 = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, clientId);
                writer2.Write(id);
                AmongUsClient.Instance.FinishRpcImmediately(writer2);
            }, 0.5f, "Fix DesyncImpostor Stuck");
            return false;
        }

        

        playerRoleClass?.OnCoEnterVent(__instance, id);

        return true;
    }
}
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoExitVent))]
class CoExitVentPatch
{
    public static void Postfix(PlayerPhysics __instance, [HarmonyArgument(0)] int id)
    {
        if (GameStates.IsHideNSeek) return;

        var player = __instance.myPlayer;
        if (Options.CurrentGameMode == CustomGameMode.FFA && FFAManager.FFA_DisableVentingWhenKCDIsUp.GetBool())
        {
            FFAManager.CoExitVent(player);
        }

        if (!AmongUsClient.Instance.AmHost) return;

        player.GetRoleClass()?.OnExitVent(player, id);
    }
}

[HarmonyPatch(typeof(GameData), nameof(GameData.CompleteTask))]
class GameDataCompleteTaskPatch
{
    public static void Postfix(PlayerControl pc)
    {
        if (GameStates.IsHideNSeek) return;

        Logger.Info($"Task Complete: {pc.GetNameWithRole().RemoveHtmlTags()}", "CompleteTask");
        Main.PlayerStates[pc.PlayerId].UpdateTask(pc);
        Utils.NotifyRoles(SpecifySeer: pc, ForceLoop: true);
        Utils.NotifyRoles(SpecifyTarget: pc, ForceLoop: true);
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
class PlayerControlCompleteTaskPatch
{
    public static bool Prefix(PlayerControl __instance)
    {
        if (GameStates.IsHideNSeek) return false;

        var player = __instance;

        if (Workhorse.OnAddTask(player))
            return false;

        return true;
    }
    public static void Postfix(PlayerControl __instance, object[] __args)
    {
        if (GameStates.IsHideNSeek) return;

        var pc = __instance;
        Snitch.OnCompleteTask(pc);
        if (pc != null && __args != null && __args.Length > 0)
        {
            int taskIndex = Convert.ToInt32(__args[0]);

            var playerTask = pc.myTasks[taskIndex];
            CustomRoleManager.OthersCompleteThisTask(pc, playerTask);
        }
        var isTaskFinish = pc.GetPlayerTaskState().IsTaskFinished;
        if (isTaskFinish && pc.Is(CustomRoles.Snitch) && pc.Is(CustomRoles.Madmate))
        {
            foreach (var impostor in Main.AllAlivePlayerControls.Where(pc => pc.Is(CustomRoleTypes.Impostor)).ToArray())
            {
                NameColorManager.Add(impostor.PlayerId, pc.PlayerId, "#ff1919");
            }
            Utils.NotifyRoles(SpecifySeer: pc);
        }
        if ((isTaskFinish &&
            pc.GetCustomRole() is CustomRoles.Doctor or CustomRoles.Sunnyboy) ||
            pc.GetCustomRole() is CustomRoles.SpeedBooster)
        {
            //ライターもしくはスピードブースターもしくはドクターがいる試合のみタスク終了時にCustomSyncAllSettingsを実行する
            Utils.MarkEveryoneDirtySettings();
        }
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ProtectPlayer))]
class PlayerControlProtectPlayerPatch
{
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        Logger.Info($"{__instance.GetNameWithRole().RemoveHtmlTags()} => {target.GetNameWithRole().RemoveHtmlTags()}", "ProtectPlayer");
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RemoveProtection))]
class PlayerControlRemoveProtectionPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        Logger.Info($"{__instance.GetNameWithRole().RemoveHtmlTags()}", "RemoveProtection");
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MixUpOutfit))]
public static class PlayerControlMixupOutfitPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        if (!__instance.IsAlive())
        {
            return;
        }

        // if player is Desync Impostor and the vanilla sees player as Imposter, the vanilla process does not hide your name, so the other person's name is hidden
        if (PlayerControl.LocalPlayer.Data.Role.IsImpostor &&  // Impostor with vanilla
            !PlayerControl.LocalPlayer.Is(CustomRoleTypes.Impostor) &&  // Not an Impostor
            Main.ResetCamPlayerList.Contains(PlayerControl.LocalPlayer.PlayerId))  // Desync Impostor
        {
            // Hide names
            __instance.cosmetics.ToggleNameVisible(false);
        }
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckSporeTrigger))]
public static class PlayerControlCheckSporeTriggerPatch
{
    public static bool Prefix()
    {
        if (AmongUsClient.Instance.AmHost)
        {
            return !Options.DisableSporeTriggerOnFungle.GetBool();
        }

        return true;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckUseZipline))]
public static class PlayerControlCheckUseZiplinePatch
{
    public static bool Prefix([HarmonyArgument(2)] bool fromTop)
    {
        if (AmongUsClient.Instance.AmHost && Options.DisableZiplineOnFungle.GetBool())
        {
            if (Options.DisableZiplineFromTop.GetBool() && fromTop) return false;
            if (Options.DisableZiplineFromUnder.GetBool() && !fromTop) return false;
        }

        return true;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Die))]
public static class PlayerControlDiePatch
{
    public static void Postfix(PlayerControl __instance)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        Main.RoleClass.Values.Where(RoleBase => RoleBase.IsEnable)
            .Do(x => x.OnOtherTargetsReducedToAtoms(__instance));

        __instance.RpcRemovePet();
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
class PlayerControlSetRolePatch
{
    public static readonly Dictionary<byte, bool> DidSetGhost = [];
    public static readonly Dictionary<PlayerControl, RoleTypes> ghostRoles = [];
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] ref RoleTypes roleType)
    {
        if (GameStates.IsHideNSeek) return true;
        try
        {
            var target = __instance;
            var targetName = __instance.GetNameWithRole().RemoveHtmlTags();
            if (!ShipStatus.Instance.enabled || !AmongUsClient.Instance.AmHost) return true;
            if (roleType is RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost)
            {
                if (DidSetGhost.ContainsKey(target.PlayerId) && DidSetGhost[target.PlayerId]) // Prevent double assignment if player gets killed as a ghost
                    return false;

                GhostRoleAssign.GhostAssignPatch(__instance); // Sets customrole ghost if succeed

                var targetIsKiller = target.Is(CustomRoleTypes.Impostor) || Main.ResetCamPlayerList.Contains(target.PlayerId);

                foreach (var seer in Main.AllPlayerControls)
                {
                    var self = seer.PlayerId == target.PlayerId;
                    var seerIsKiller = seer.Is(CustomRoleTypes.Impostor) || Main.ResetCamPlayerList.Contains(seer.PlayerId);
                    if (!ghostRoles.ContainsKey(seer))
                        ghostRoles.Add(seer, roleType);

                    if (target.IsAnySubRole(x => x.IsGhostRole()) || target.GetCustomRole().IsGhostRole())
                    {
                        ghostRoles[seer] = RoleTypes.GuardianAngel;
                    }
                    else if ((self && targetIsKiller) || (!seerIsKiller && target.Is(CustomRoleTypes.Impostor)))
                    {
                        ghostRoles[seer] = RoleTypes.ImpostorGhost;
                    }
                    else
                    {
                        ghostRoles[seer] = RoleTypes.CrewmateGhost;
                    }
                }
                if (target.IsAnySubRole(x => x.IsGhostRole()) || target.GetCustomRole().IsGhostRole())
                {
                    roleType = RoleTypes.GuardianAngel;
                    return true;
                }
                else if (ghostRoles.All(kvp => kvp.Value == RoleTypes.CrewmateGhost))
                {
                    roleType = RoleTypes.CrewmateGhost;
                    return true;
                }
                else if (ghostRoles.All(kvp => kvp.Value == RoleTypes.ImpostorGhost))
                {
                    roleType = RoleTypes.ImpostorGhost;
                    return true;
                }
                else
                {
                    foreach ((var seer, var role) in ghostRoles) // Idk wtf this for tbh
                    {
                        Logger.Info($"Desync {targetName} => {role} for {seer.GetNameWithRole().RemoveHtmlTags()}", "PlayerControl.RpcSetRole");
                        target.RpcSetRoleDesync(role, seer.GetClientId());
                    }
                    return false;
                }
            }
        }
        catch (Exception error) 
        { 
            Logger.Warn($"Error After RpcSetRole: {error}", "RpcSetRole Prefix"); 
        }

        return true;
    }
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] ref RoleTypes roleType, bool __runOriginal)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (roleType == RoleTypes.GuardianAngel && !DidSetGhost.ContainsKey(__instance.PlayerId)) 
        {
            _ = new LateTask(() => { 
                
                __instance.RpcResetAbilityCooldown();
                
                if (Options.SendRoleDescriptionFirstMeeting.GetBool())
                {
                    var host = PlayerControl.LocalPlayer;
                    var name = host.Data.PlayerName;
                    var lp = __instance;
                    var sb = new StringBuilder();
                    var role = __instance.GetCustomRole();
                    sb.Append(GetString(role.ToString()) + Utils.GetRoleMode(role) + lp.GetRoleInfo(true));
                    if (Options.CustomRoleSpawnChances.TryGetValue(role, out var opt))
                        Utils.ShowChildrenSettings(Options.CustomRoleSpawnChances[role], ref sb, command: true);
                    var txt = sb.ToString();
                    sb.Clear().Append(txt.RemoveHtmlTags());
                    foreach (var subRole in Main.PlayerStates[lp.PlayerId].SubRoles.ToArray())
                        sb.Append($"\n\n" + GetString($"{subRole}") + Utils.GetRoleMode(subRole) + GetString($"{subRole}InfoLong"));
                    var writer = CustomRpcSender.Create("SendGhostRoleInfo", SendOption.None);
                    writer.StartMessage(__instance.GetClientId());
                    writer.StartRpc(host.NetId, (byte)RpcCalls.SetName)
                        .Write(Utils.ColorString(Utils.GetRoleColor(role), GetString("GhostTransformTitle")))
                        .EndRpc();
                    writer.StartRpc(host.NetId, (byte)RpcCalls.SendChat)
                        .Write(sb.ToString())
                        .EndRpc();
                    writer.StartRpc(host.NetId, (byte)RpcCalls.SetName)
                        .Write(name)
                        .EndRpc();
                    writer.EndMessage();
                    writer.SendMessage();
                    host.Notify("", 0.1f);

                   // Utils.SendMessage(sb.ToString(), __instance.PlayerId, Utils.ColorString(Utils.GetRoleColor(role), GetString("GhostTransformTitle")));

                }

            }, 0.1f, "SetGuardianAngel");
        }

        if (__runOriginal)
        {
            Logger.Info($" {__instance.GetRealName()} => {roleType}", "PlayerControl.RpcSetRole");

            if (roleType is RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost or RoleTypes.GuardianAngel)
                if (!DidSetGhost.ContainsKey(__instance.PlayerId))
                    DidSetGhost.Add(__instance.PlayerId, true);
        }
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetRole))]
class PlayerControlLocalSetRolePatch
{
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] RoleTypes role)
    {
        if (!AmongUsClient.Instance.AmHost && GameStates.IsNormalGame && !GameStates.IsModHost)
        {
            var modRole = role switch
            {
                RoleTypes.Impostor => CustomRoles.ImpostorTOHE,
                RoleTypes.Shapeshifter => CustomRoles.ShapeshifterTOHE,
                RoleTypes.Crewmate => CustomRoles.CrewmateTOHE,
                RoleTypes.Engineer => CustomRoles.EngineerTOHE,
                RoleTypes.Scientist => CustomRoles.ScientistTOHE,
                _ => CustomRoles.NotAssigned,
            };
            if (modRole != CustomRoles.NotAssigned)
            {
                Main.PlayerStates[__instance.PlayerId].SetMainRole(modRole);
            }
        }
    }
}
