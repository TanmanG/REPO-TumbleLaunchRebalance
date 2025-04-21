using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TumbleLaunchRebalance;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private static bool _patchFailed;
    private static ConfigEntry<int> _configTumbleDamageOnHitEnemy;
    private static ConfigEntry<int> _configTumbleUpgradesNeededForMaxReduction;
    private static ConfigEntry<float> _configTumbleUpgradeMaxDamageReduction;
    
    internal static new ManualLogSource Logger;

    private void LoadConfig()
    {
        // Read the config
        _configTumbleDamageOnHitEnemy = Config.Bind("Balance",
                                                  "TumbleDamageOnHitEnemy",
                                                  0,
                                                  "The amount of damage the player is dealt when hitting an enemy while tumbling.");
        
        _configTumbleUpgradesNeededForMaxReduction = Config.Bind("Balance",
                                                               "TumbleUpgradesNeededForMaxReduction",
                                                               8,
                                                               "The number of Tumble Launch upgrades the player needs to reach the max damage reduction. E.g. 5 means the player will reach maximum tumble damage reduction on their 5th upgrade.");
        
        _configTumbleUpgradeMaxDamageReduction = Config.Bind("Balance",
                                                             "MaxDamageReduction",
                                                             1.0f,
                                                             "The maximum reduction to damage (while tumbling) that can be attained. E.g. 0.75 means the player can reach 75% reduction in damage, in which 100 damage would become -> 25.");
    }
        
    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loading...");
        
        // Load the config.
        LoadConfig();
        
        // Apply the patches
        Harmony harmony = Harmony.CreateAndPatchAll(typeof(Plugin));
            
        // Check if the patch failed
        if (_patchFailed)
        {
            // Log the failure.
            Logger.LogFatal("Failure to patch, reverting!");
            // Unpatch the plugin.
            Harmony.UnpatchID(harmony.Id);
            return;
        }
            
        // Log plugin load success
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} loaded!");
    }

    [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.HitEnemy))]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> PatchTumbleDamage(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            // Create a matcher to seek around the instructions.
            CodeMatcher matcher = new CodeMatcher(instructions);
            
            // Seek to the first hurt-player line of code.
            matcher.MatchForward(true,
                                 new CodeMatch(OpCodes.Ldarg_0),
                                 new CodeMatch(OpCodes.Ldfld),
                                 new CodeMatch(OpCodes.Ldfld),
                                 new CodeMatch(OpCodes.Ldc_I4_5));
            
            // Check if the matcher failed to find the location.
            if (matcher.IsValid == false)
            {
                Logger.LogFatal($"Failed to find patch location for first HitEnemy, reverting!");
                _patchFailed = true;
                return instructions;
            }

            // Replace the damage dealt instruction with the config value.
            matcher.Set(OpCodes.Ldc_I4, _configTumbleDamageOnHitEnemy.Value);


            // Seek to the second hurt-player line of code.
            matcher.MatchForward(true,
                                 new CodeMatch(OpCodes.Ldarg_0),
                                 new CodeMatch(OpCodes.Ldfld),
                                 new CodeMatch(OpCodes.Ldfld),
                                 new CodeMatch(OpCodes.Ldc_I4_5));
            
            // Check if the matcher failed to find the location.
            if (matcher.IsValid == false)
            {
                Logger.LogFatal($"Failed to find patch location for second HitEnemy, reverting!");
                _patchFailed = true;
                return instructions;
            }

            // Replace the damage dealt instruction with the config value.
            matcher.Set(OpCodes.Ldc_I4, _configTumbleDamageOnHitEnemy.Value);
            
            return matcher.InstructionEnumeration();
        }
        catch (Exception ex)
        {
            Logger.LogFatal($"Exception caught while patching HitEnemy, reverting! Message: {ex.Message}");
            _patchFailed = true;
            return instructions;
        }     
    }
    
    [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.BreakImpact))]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> PatchTumbleDamageReduction(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            // Create a matcher to seek around the instructions.
            CodeMatcher matcher = new CodeMatcher(instructions);

            /*
            IL_003d: ldarg.0      // this
            IL_003e: ldfld        class PlayerAvatar PlayerTumble::playerAvatar
            IL_0043: ldfld        class PlayerHealth PlayerAvatar::playerHealth
            IL_0048: ldarg.0      // this
            IL_0049: ldfld        int32 PlayerTumble::impactHurtDamage
            IL_004e: ldc.i4.1
            IL_004f: ldc.i4.m1
            IL_0050: callvirt     instance void PlayerHealth::Hurt(int32, bool, int32)
            */

            // Seek to the hurt-player line of code, after the damage is loaded onto the stack.
            matcher.MatchForward(true,
                                 new CodeMatch(OpCodes.Ldarg_0),
                                 new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlayerTumble), nameof(PlayerTumble.playerAvatar))),
                                 new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlayerAvatar), nameof(PlayerAvatar.playerHealth))),
                                 new CodeMatch(OpCodes.Ldarg_0),
                                 new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlayerTumble), "impactHurtDamage")));
            matcher.Advance(1);
            
            // Check if the matcher failed to find the location.
            if (matcher.IsValid == false)
            {
                Logger.LogFatal($"Failed to find patch location for Damage Reduction Patch, reverting!");
                _patchFailed = true;
                return instructions;
            }
            
            // Move the patcher forward one.
            matcher.Advance(1);

            // Insert the code to reduce the damage dealt to the player.
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0));
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call,
                                                         AccessTools.Method(typeof(Plugin), nameof(CalculateTumbleDamage))));

            matcher.Start();

            // Return the modified instructions.
            return matcher.InstructionEnumeration();
        }
        catch (Exception ex)
        {
            Logger.LogFatal($"Exception caught while patching BreakImpact, reverting! Message: {ex.Message}");
            _patchFailed = true;
            return instructions;
        }
    }
    
    public static int CalculateTumbleDamage(int damage, PlayerTumble tumble)
    {
        try
        {
            // Check if the number of upgrades needed are zero or less, which means we're automatically at max damage reduction.
            if (_configTumbleUpgradesNeededForMaxReduction.Value == 0)
            {
                return (int) (1 - _configTumbleUpgradeMaxDamageReduction.Value) * damage;
            }

            // Get the number of tumble upgrades owned by the player.
            int tumbleUpgradesOwned =
                StatsManager.instance.playerUpgradeLaunch[SemiFunc.PlayerGetSteamID(tumble.playerAvatar)];

            // Calculate and return the damage reduction.
            float damageRatio = 1 - _configTumbleUpgradeMaxDamageReduction.Value
                                        * Math.Min(tumbleUpgradesOwned / _configTumbleUpgradesNeededForMaxReduction.Value,
                                                   1);

            // Clamp the damage reduction to be between 0 and the max damage reduction.
            return (int) damageRatio * damage;
        }
        catch (Exception ex)
        {
            Logger.LogFatal($"Exception thrown while calculating tumble damage reduction! Message: {ex.Message}");
            return damage;
        }
    }
}
