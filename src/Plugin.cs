using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace OstranautsBatterySwap
{
    [BepInPlugin("com.dezgard.ostranauts.batteryswap", "Ostranauts Battery Swap", "0.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; }
        internal static StreamWriter FileLog { get; private set; }
        internal static ConfigEntry<double> SwapThresholdPercent { get; private set; }
        internal static ConfigEntry<double> SwapCooldownSeconds { get; private set; }
        internal static ConfigEntry<bool> IncludeDragSlot { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            SwapThresholdPercent = Config.Bind("Battery Swap", "SwapThresholdPercent", 10.0, "Swap held tool batteries at or below this charge percent.");
            SwapCooldownSeconds = Config.Bind("Battery Swap", "SwapCooldownSeconds", 2.0, "Minimum seconds between swap attempts for the same tool.");
            IncludeDragSlot = Config.Bind("Battery Swap", "IncludeDragSlot", false, "Also check a powered tool in the drag slot.");

            try
            {
                var dir = Path.Combine(Paths.BepInExRootPath, "BatterySwapLogs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "BatteryAutoSwap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                FileLog = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                SwapLog.Write("START", "Ostranauts Battery Swap 0.1.0 file=" + path);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Battery swap file setup failed: " + ex.GetType().Name + " " + ex.Message);
            }

            _harmony = new Harmony("com.dezgard.ostranauts.batteryswap");
            _harmony.PatchAll();
            SwapLog.Write("LOADED", "Auto battery swap enabled. Held tools only; carried spare batteries only.");
        }

        private void OnDestroy()
        {
            try
            {
                _harmony?.UnpatchSelf();
                SwapLog.Write("STOP", "Battery swap unloaded.");
                FileLog?.Flush();
                FileLog?.Dispose();
            }
            catch
            {
            }
        }
    }

    internal static class SwapLog
    {
        internal static void Write(string tag, string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + tag + "] " + message;
            Plugin.Log?.LogInfo(line);

            try
            {
                Plugin.FileLog?.WriteLine(line);
            }
            catch
            {
            }
        }
    }

    internal static class BatterySwap
    {
        private static readonly HashSet<string> BatteryConds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IsBatteryDrill01",
            "IsBatteryWelder01",
            "IsBattery04",
            "IsBatteryDisp01",
            "IsBatteryEVA"
        };

        private static readonly Dictionary<string, DateTime> LastAttemptUtcByTool = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal static void TrySwapHeldTools(CondOwner actor, string reason)
        {
            if (actor == null)
                return;

            foreach (var tool in HeldTools(actor))
            {
                TrySwapTool(actor, tool, reason);
            }
        }

        private static void TrySwapTool(CondOwner actor, CondOwner tool, string reason)
        {
            try
            {
                if (!IsPoweredTool(tool))
                    return;

                var key = tool.strID ?? tool.strName ?? tool.strItemDef ?? "?";
                var now = DateTime.UtcNow;
                if (LastAttemptUtcByTool.TryGetValue(key, out var last)
                    && (now - last).TotalSeconds < Math.Max(0.25, Plugin.SwapCooldownSeconds.Value))
                {
                    return;
                }

                var currentBattery = ToolBattery(tool);
                if (!BatteryIsLow(currentBattery))
                    return;

                LastAttemptUtcByTool[key] = now;

                var spare = FindBestCompatibleSpare(actor, tool, currentBattery);
                if (spare == null)
                {
                    SwapLog.Write("NO_SPARE", "actor=" + Name(actor)
                        + " tool={" + Item(tool) + "}"
                        + " current={" + Item(currentBattery) + "}"
                        + " reason=" + reason);
                    return;
                }

                SwapLog.Write("SWAP_BEGIN", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " old={" + Item(currentBattery) + "}"
                    + " spare={" + Item(spare) + "}"
                    + " reason=" + reason);

                if (!SwapBattery(actor, tool, currentBattery, spare))
                {
                    SwapLog.Write("SWAP_FAIL", "actor=" + Name(actor)
                        + " tool={" + Item(tool) + "}"
                        + " old={" + Item(currentBattery) + "}"
                        + " spare={" + Item(spare) + "}");
                    return;
                }

                SwapLog.Write("SWAP_DONE", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " now={" + Item(ToolBattery(tool)) + "}");
            }
            catch (Exception ex)
            {
                SwapLog.Write("SWAP_ERROR", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " error=" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static bool SwapBattery(CondOwner actor, CondOwner tool, CondOwner oldBattery, CondOwner spare)
        {
            var sourceParent = spare.objCOParent;
            var removedSpare = RemoveFromParent(spare);
            if (removedSpare == null)
                return false;

            CondOwner removedOld = null;
            if (oldBattery != null)
            {
                removedOld = tool.objContainer?.RemoveCO(oldBattery, bForce: false) ?? tool.RemoveCO(oldBattery, bForce: false);
                if (removedOld == null)
                {
                    PutBack(sourceParent, actor, removedSpare);
                    return false;
                }
            }

            if (!AddToTool(tool, removedSpare))
            {
                PutBack(sourceParent, actor, removedSpare);
                if (removedOld != null)
                    AddToTool(tool, removedOld);
                return false;
            }

            if (removedOld != null)
                PutBack(sourceParent, actor, removedOld);

            return true;
        }

        private static CondOwner RemoveFromParent(CondOwner item)
        {
            if (item == null)
                return null;

            var parent = item.objCOParent;
            if (parent == null)
                return item;

            try
            {
                return parent.RemoveCO(item, bForce: false);
            }
            catch (Exception ex)
            {
                SwapLog.Write("REMOVE_ERROR", "parent={" + Item(parent) + "} item={" + Item(item) + "} error=" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        private static bool AddToTool(CondOwner tool, CondOwner battery)
        {
            if (tool?.objContainer == null || battery == null)
                return false;

            try
            {
                var pair = new PairXY { x = 0, y = 0 };
                tool.objContainer.AddCOSimple(battery, pair);
                return battery.objCOParent == tool;
            }
            catch (Exception ex)
            {
                SwapLog.Write("ADD_TOOL_ERROR", "tool={" + Item(tool) + "} battery={" + Item(battery) + "} error=" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static void PutBack(CondOwner preferredParent, CondOwner actor, CondOwner item)
        {
            if (item == null)
                return;

            if (TryAddToParent(preferredParent, item))
                return;

            if (TryAddToParent(actor, item))
                return;

            SwapLog.Write("PUTBACK_FAIL", "preferred={" + Item(preferredParent) + "} actor=" + Name(actor) + " item={" + Item(item) + "}");
        }

        private static bool TryAddToParent(CondOwner parent, CondOwner item)
        {
            if (parent == null || item == null)
                return false;

            try
            {
                var result = parent.AddCO(item, bEquip: false, bOverflow: true, bIgnoreLocks: true);
                return result != null && item.objCOParent != null;
            }
            catch (Exception ex)
            {
                SwapLog.Write("PUTBACK_ERROR", "parent={" + Item(parent) + "} item={" + Item(item) + "} error=" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static IEnumerable<CondOwner> HeldTools(CondOwner actor)
        {
            if (actor?.compSlots == null)
                yield break;

            foreach (var slot in HeldSlotNames())
            {
                List<CondOwner> items;
                try
                {
                    items = actor.compSlots.GetCOs(slot, bAllowLocked: true, objCondTrig: null);
                }
                catch
                {
                    continue;
                }

                if (items == null)
                    continue;

                foreach (var item in items)
                {
                    if (item != null)
                        yield return item;
                }
            }
        }

        private static IEnumerable<string> HeldSlotNames()
        {
            yield return "heldL";
            yield return "heldR";

            if (Plugin.IncludeDragSlot.Value)
                yield return "drag";
        }

        private static CondOwner ToolBattery(CondOwner tool)
        {
            if (tool?.objContainer == null)
                return null;

            try
            {
                return tool.objContainer.GetCOs(bAllowLocked: true, objCondTrig: null)
                    .FirstOrDefault(IsBattery);
            }
            catch
            {
                return null;
            }
        }

        private static CondOwner FindBestCompatibleSpare(CondOwner actor, CondOwner tool, CondOwner currentBattery)
        {
            var currentId = currentBattery?.strID;

            return GetCarried(actor)
                .Where(IsBattery)
                .Where(b => !string.Equals(b.strID, currentId, StringComparison.OrdinalIgnoreCase))
                .Where(b => IsUsableSpare(actor, b))
                .Where(b => Compatible(tool, b))
                .Where(b => PowerRatio(b) > Math.Max(Plugin.SwapThresholdPercent.Value / 100.0, 0.01))
                .OrderByDescending(PowerRatio)
                .FirstOrDefault();
        }

        private static IEnumerable<CondOwner> GetCarried(CondOwner actor)
        {
            try
            {
                return actor?.GetCOs(bAllowLocked: true, objCondTrig: null) ?? new List<CondOwner>();
            }
            catch
            {
                return new List<CondOwner>();
            }
        }

        private static bool IsUsableSpare(CondOwner actor, CondOwner battery)
        {
            if (battery == null || battery.objCOParent == null)
                return false;

            if (!SafeHas(battery, "IsCarried"))
                return false;

            var parent = battery.objCOParent;
            if (IsPoweredTool(parent))
                return false;

            if (IsBatteryCharger(parent))
                return false;

            return IsDescendantOf(battery, actor);
        }

        private static bool Compatible(CondOwner tool, CondOwner battery)
        {
            if (tool?.objContainer == null || battery == null)
                return false;

            try
            {
                return tool.objContainer.AllowedCO(battery);
            }
            catch
            {
                return SameBatteryContainerHint(tool, battery);
            }
        }

        private static bool SameBatteryContainerHint(CondOwner tool, CondOwner battery)
        {
            var allowed = tool?.objContainer?.ctAllowed?.strName ?? "";
            if (allowed.IndexOf("Drill01Battery", StringComparison.OrdinalIgnoreCase) >= 0)
                return SafeHas(battery, "IsBatteryDrill01");
            if (allowed.IndexOf("Welder01Battery", StringComparison.OrdinalIgnoreCase) >= 0)
                return SafeHas(battery, "IsBatteryWelder01");
            if (allowed.IndexOf("EVABattery", StringComparison.OrdinalIgnoreCase) >= 0)
                return SafeHas(battery, "IsBatteryEVA");
            if (allowed.IndexOf("Battery04", StringComparison.OrdinalIgnoreCase) >= 0)
                return SafeHas(battery, "IsBattery04");
            if (allowed.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) >= 0)
                return IsBattery(battery);
            return false;
        }

        private static bool BatteryIsLow(CondOwner battery)
        {
            if (battery == null)
                return true;

            var max = CondAmount(battery, "StatPowerMax");
            if (max <= 0)
                return false;

            return CondAmount(battery, "StatPower") / max <= Plugin.SwapThresholdPercent.Value / 100.0;
        }

        private static bool IsPoweredTool(CondOwner co)
        {
            if (co?.objContainer == null)
                return false;

            var allowed = co.objContainer.ctAllowed?.strName ?? "";
            if (allowed.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var def = co.strItemDef ?? co.strName ?? "";
            return def.StartsWith("ItmTool", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBattery(CondOwner co)
        {
            if (co == null)
                return false;

            foreach (var cond in BatteryConds)
            {
                if (SafeHas(co, cond))
                    return true;
            }

            var def = co.strItemDef ?? co.strName ?? "";
            return def.IndexOf("ItmBattery", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsBatteryCharger(CondOwner co)
        {
            var def = co?.strItemDef ?? co?.strName ?? "";
            var friendly = co?.strNameFriendly ?? "";
            return def.IndexOf("Charger", StringComparison.OrdinalIgnoreCase) >= 0
                || friendly.IndexOf("Battery Charger", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDescendantOf(CondOwner child, CondOwner root)
        {
            var current = child;
            for (var i = 0; i < 32 && current != null; i++)
            {
                if (ReferenceEquals(current, root))
                    return true;

                current = current.objCOParent;
            }

            return false;
        }

        private static double PowerRatio(CondOwner battery)
        {
            var max = CondAmount(battery, "StatPowerMax");
            if (max <= 0)
                return 0;

            return CondAmount(battery, "StatPower") / max;
        }

        private static double CondAmount(CondOwner co, string cond)
        {
            try
            {
                return co != null && co.HasCond(cond) ? co.GetCondAmount(cond) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool SafeHas(CondOwner co, string cond)
        {
            try
            {
                return co != null && co.HasCond(cond);
            }
            catch
            {
                return false;
            }
        }

        private static string Item(CondOwner co)
        {
            if (co == null)
                return "<null>";

            var name = string.IsNullOrEmpty(co.strNameFriendly) ? co.strName : co.strNameFriendly;
            return (co.strID ?? "?") + "/" + (name ?? "?")
                + " def=" + (co.strItemDef ?? co.strName ?? "?")
                + " parent=" + Name(co.objCOParent)
                + " power=" + CondAmount(co, "StatPower").ToString("0.######") + "/" + CondAmount(co, "StatPowerMax").ToString("0.######");
        }

        private static string Name(CondOwner co)
        {
            if (co == null)
                return "<null>";

            return string.IsNullOrEmpty(co.strNameFriendly) ? (co.strName ?? "?") : co.strNameFriendly;
        }
    }

    [HarmonyPatch(typeof(CondOwner), "QueueInteraction")]
    internal static class QueueInteractionPatch
    {
        private static void Prefix(CondOwner __instance, Interaction objInteraction)
        {
            BatterySwap.TrySwapHeldTools(__instance, objInteraction?.strName ?? "QueueInteraction");
        }
    }
}
