using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace OstranautsBatterySwap
{
    [BepInPlugin("com.dezgard.ostranauts.batteryswap", "Ostranauts Battery Swap", "0.2.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; }
        internal static StreamWriter FileLog { get; private set; }
        internal static ConfigEntry<double> SwapThresholdPercent { get; private set; }
        internal static ConfigEntry<double> SwapCooldownSeconds { get; private set; }
        internal static ConfigEntry<bool> IncludeDragSlot { get; private set; }
        internal static ConfigEntry<bool> UseShipChargers { get; private set; }
        internal static ConfigEntry<bool> WalkToShipChargers { get; private set; }
        internal static ConfigEntry<double> ChargerUseRangeTiles { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            SwapThresholdPercent = Config.Bind("Battery Swap", "SwapThresholdPercent", 10.0, "Swap held tool batteries at or below this charge percent.");
            SwapCooldownSeconds = Config.Bind("Battery Swap", "SwapCooldownSeconds", 2.0, "Minimum seconds between swap attempts for the same tool.");
            IncludeDragSlot = Config.Bind("Battery Swap", "IncludeDragSlot", false, "Also check a powered tool in the drag slot.");
            UseShipChargers = Config.Bind("Battery Swap", "UseShipChargers", true, "Use compatible charged batteries from loaded ship chargers when no carried spare is available.");
            WalkToShipChargers = Config.Bind("Battery Swap", "WalkToShipChargers", true, "Walk to ship chargers before swapping charger batteries instead of swapping remotely.");
            ChargerUseRangeTiles = Config.Bind("Battery Swap", "ChargerUseRangeTiles", 1.25, "How close the character must be to a charger before BBG swaps a charger battery.");

            try
            {
                var dir = Path.Combine(Paths.BepInExRootPath, "BatterySwapLogs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "BatteryAutoSwap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                FileLog = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                SwapLog.Write("START", "Ostranauts Battery Swap 0.2.2 file=" + path);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Battery swap file setup failed: " + ex.GetType().Name + " " + ex.Message);
            }

            _harmony = new Harmony("com.dezgard.ostranauts.batteryswap");
            _harmony.PatchAll();
            SwapLog.Write("LOADED", "Auto battery swap enabled. Held tools, carried spare batteries, and walk-to-charger fallback.");
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
        private static readonly Dictionary<string, PendingChargerSwap> PendingChargerSwaps = new Dictionary<string, PendingChargerSwap>(StringComparer.OrdinalIgnoreCase);
        private static int InternalQueueDepth;

        internal static bool IsInternalQueueing => InternalQueueDepth > 0;

        private sealed class BatterySource
        {
            internal CondOwner Battery;
            internal CondOwner SourceParent;
            internal string Kind;

            internal bool IsCharger => string.Equals(Kind, "charger", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class PendingChargerSwap
        {
            internal string ActorId;
            internal string ToolId;
            internal string ChargerId;
            internal string OriginalTargetId;
            internal string OriginalInteractionName;
            internal bool OriginalWasManual;
            internal DateTime CreatedUtc;
        }

        internal static bool TrySwapHeldTools(CondOwner actor, string reason, CondOwner originalTarget, Interaction originalInteraction)
        {
            if (IsInternalQueueing)
                return true;

            if (actor == null)
                return true;

            foreach (var tool in HeldTools(actor))
            {
                if (!TrySwapTool(actor, tool, reason, originalTarget, originalInteraction))
                    return false;
            }

            return true;
        }

        internal static void TryCompletePending(CondOwner actor, string reason)
        {
            if (IsInternalQueueing || actor == null)
                return;

            var key = actor.strID ?? actor.strName;
            if (string.IsNullOrEmpty(key) || !PendingChargerSwaps.TryGetValue(key, out var pending))
                return;

            if ((DateTime.UtcNow - pending.CreatedUtc).TotalSeconds > 180.0)
            {
                PendingChargerSwaps.Remove(key);
                SwapLog.Write("CHARGER_PENDING_EXPIRED", "actor=" + Name(actor) + " reason=" + reason);
                return;
            }

            var tool = ResolveCO(pending.ToolId);
            var charger = ResolveCO(pending.ChargerId);
            if (tool == null || charger == null)
            {
                PendingChargerSwaps.Remove(key);
                SwapLog.Write("CHARGER_PENDING_MISSING", "actor=" + Name(actor)
                    + " toolId=" + pending.ToolId
                    + " chargerId=" + pending.ChargerId
                    + " reason=" + reason);
                return;
            }

            if (!IsNear(actor, charger))
                return;

            var currentBattery = ToolBattery(tool);
            if (currentBattery == null)
            {
                PendingChargerSwaps.Remove(key);
                SwapLog.Write("CHARGER_PENDING_EMPTY_TOOL", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " charger={" + Item(charger) + "}"
                    + " reason=" + reason);
                RequeueOriginal(actor, pending);
                return;
            }

            if (!BatteryIsLow(currentBattery))
            {
                PendingChargerSwaps.Remove(key);
                SwapLog.Write("CHARGER_PENDING_NOT_NEEDED", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " current={" + Item(currentBattery) + "}"
                    + " reason=" + reason);
                RequeueOriginal(actor, pending);
                return;
            }

            var source = ChargerSourceFrom(charger, tool, currentBattery);
            if (source == null)
            {
                PendingChargerSwaps.Remove(key);
                SwapLog.Write("CHARGER_PENDING_NO_SOURCE", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " charger={" + Item(charger) + "}"
                    + " reason=" + reason);
                return;
            }

            SwapLog.Write("CHARGER_ARRIVED", "actor=" + Name(actor)
                + " tool={" + Item(tool) + "}"
                + " charger={" + Item(charger) + "}"
                + " spare={" + Item(source.Battery) + "}"
                + " reason=" + reason);

            if (!SwapBattery(actor, tool, currentBattery, source))
            {
                PendingChargerSwaps.Remove(key);
                SwapLog.Write("CHARGER_SWAP_FAIL", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " charger={" + Item(charger) + "}"
                    + " spare={" + Item(source.Battery) + "}");
                return;
            }

            PendingChargerSwaps.Remove(key);
            SwapLog.Write("SWAP_DONE", "actor=" + Name(actor)
                + " tool={" + Item(tool) + "}"
                + " now={" + Item(ToolBattery(tool)) + "}"
                + " source=charger_walk");
            RequeueOriginal(actor, pending);
        }

        private static bool TrySwapTool(CondOwner actor, CondOwner tool, string reason, CondOwner originalTarget, Interaction originalInteraction)
        {
            try
            {
                if (!IsPoweredTool(tool))
                    return true;

                var actorKey = actor.strID ?? actor.strName;
                if (!string.IsNullOrEmpty(actorKey) && PendingChargerSwaps.ContainsKey(actorKey))
                    return true;

                var key = tool.strID ?? tool.strName ?? tool.strItemDef ?? "?";
                var now = DateTime.UtcNow;
                if (LastAttemptUtcByTool.TryGetValue(key, out var last)
                    && (now - last).TotalSeconds < Math.Max(0.25, Plugin.SwapCooldownSeconds.Value))
                {
                    return true;
                }

                var currentBattery = ToolBattery(tool);
                if (!BatteryIsLow(currentBattery))
                    return true;

                LastAttemptUtcByTool[key] = now;

                var source = FindBestCompatibleSource(actor, tool, currentBattery);
                if (source == null)
                {
                    if (currentBattery == null)
                    {
                        SwapLog.Write("EMPTY_TOOL_NO_CARRIED_SPARE", "actor=" + Name(actor)
                            + " tool={" + Item(tool) + "}"
                            + " reason=" + reason);
                        return true;
                    }

                    SwapLog.Write("NO_SPARE", "actor=" + Name(actor)
                        + " tool={" + Item(tool) + "}"
                        + " current={" + Item(currentBattery) + "}"
                        + " reason=" + reason);
                    return true;
                }

                if (source.IsCharger && Plugin.WalkToShipChargers.Value && !IsNear(actor, source.SourceParent))
                {
                    return !QueueWalkToCharger(actor, tool, currentBattery, source, originalTarget, originalInteraction, reason);
                }

                SwapLog.Write("SWAP_BEGIN", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " old={" + Item(currentBattery) + "}"
                    + " spare={" + Item(source.Battery) + "}"
                    + " source=" + source.Kind
                    + " sourceParent={" + Item(source.SourceParent) + "}"
                    + " reason=" + reason);

                if (!SwapBattery(actor, tool, currentBattery, source))
                {
                    SwapLog.Write("SWAP_FAIL", "actor=" + Name(actor)
                        + " tool={" + Item(tool) + "}"
                        + " old={" + Item(currentBattery) + "}"
                        + " spare={" + Item(source.Battery) + "}"
                        + " source=" + source.Kind);
                    return true;
                }

                SwapLog.Write("SWAP_DONE", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " now={" + Item(ToolBattery(tool)) + "}");
                return true;
            }
            catch (Exception ex)
            {
                SwapLog.Write("SWAP_ERROR", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " error=" + ex.GetType().Name + " " + ex.Message);
                return true;
            }
        }

        private static bool SwapBattery(CondOwner actor, CondOwner tool, CondOwner oldBattery, BatterySource source)
        {
            var removedSpare = RemoveFromParent(source.Battery);
            if (removedSpare == null)
                return false;

            CondOwner removedOld = null;
            if (oldBattery != null)
            {
                removedOld = tool.objContainer?.RemoveCO(oldBattery, bForce: false) ?? tool.RemoveCO(oldBattery, bForce: false);
                if (removedOld == null)
                {
                    PutBackToSource(source, actor, removedSpare);
                    return false;
                }
            }

            if (!AddToTool(tool, removedSpare))
            {
                PutBackToSource(source, actor, removedSpare);
                if (removedOld != null)
                    AddToTool(tool, removedOld);
                return false;
            }

            if (removedOld != null)
                PutOldBattery(actor, source, removedOld);

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

        private static bool AddToContainer(CondOwner containerOwner, CondOwner item)
        {
            if (containerOwner?.objContainer == null || item == null)
                return false;

            try
            {
                var pair = new PairXY { x = 0, y = 0 };
                containerOwner.objContainer.AddCOSimple(item, pair);
                return IsDescendantOf(item, containerOwner);
            }
            catch (Exception ex)
            {
                SwapLog.Write("ADD_CONTAINER_ERROR", "container={" + Item(containerOwner) + "} item={" + Item(item) + "} error=" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static void PutBackToSource(BatterySource source, CondOwner actor, CondOwner item)
        {
            if (source != null && source.IsCharger && AddToContainer(source.SourceParent, item))
                return;

            PutBack(source?.SourceParent, actor, item);
        }

        private static void PutOldBattery(CondOwner actor, BatterySource source, CondOwner oldBattery)
        {
            if (source != null && source.IsCharger)
            {
                if (CanAcceptBattery(source.SourceParent, oldBattery) && AddToContainer(source.SourceParent, oldBattery))
                {
                    SwapLog.Write("PUT_OLD_IN_CHARGER", "charger={" + Item(source.SourceParent) + "} old={" + Item(oldBattery) + "}");
                    return;
                }

                PutBack(actor, actor, oldBattery);
                return;
            }

            PutBack(source?.SourceParent, actor, oldBattery);
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
                if (IsDescendantOf(item, parent))
                    return true;

                if (IsBatteryCharger(parent) && CanAcceptBattery(parent, item))
                    return AddToContainer(parent, item);

                var result = parent.AddCO(item, bEquip: false, bOverflow: true, bIgnoreLocks: true);
                return IsDescendantOf(item, parent) || (result != null && item.objCOParent != null);
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

        private static BatterySource FindBestCompatibleSource(CondOwner actor, CondOwner tool, CondOwner currentBattery)
        {
            var carried = FindBestCarriedSpare(actor, tool, currentBattery);
            if (carried != null)
                return carried;

            if (currentBattery == null)
                return null;

            if (!Plugin.UseShipChargers.Value)
                return null;

            return FindBestChargerSpare(actor, tool, currentBattery);
        }

        private static BatterySource FindBestCarriedSpare(CondOwner actor, CondOwner tool, CondOwner currentBattery)
        {
            var currentId = currentBattery?.strID;

            var spare = GetCarried(actor)
                .Where(IsBattery)
                .Where(b => !string.Equals(b.strID, currentId, StringComparison.OrdinalIgnoreCase))
                .Where(b => IsUsableSpare(actor, b))
                .Where(b => Compatible(tool, b))
                .Where(b => PowerRatio(b) > Math.Max(Plugin.SwapThresholdPercent.Value / 100.0, 0.01))
                .OrderByDescending(PowerRatio)
                .FirstOrDefault();

            return spare == null
                ? null
                : new BatterySource
                {
                    Battery = spare,
                    SourceParent = spare.objCOParent,
                    Kind = "carried"
                };
        }

        private static BatterySource FindBestChargerSpare(CondOwner actor, CondOwner tool, CondOwner currentBattery)
        {
            var currentId = currentBattery?.strID;
            var minimumRatio = Math.Max(Plugin.SwapThresholdPercent.Value / 100.0, 0.01);
            var bestRatio = minimumRatio;
            BatterySource best = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var charger in GetActorShipCOs(actor).Where(IsBatteryCharger))
            {
                var key = charger.strID ?? charger.GetHashCode().ToString();
                if (!seen.Add(key))
                    continue;

                var battery = ChargerBattery(charger);
                if (battery == null
                    || string.Equals(battery.strID, currentId, StringComparison.OrdinalIgnoreCase)
                    || !Compatible(tool, battery)
                    || (currentBattery != null && !CanAcceptBattery(charger, currentBattery)))
                {
                    continue;
                }

                var ratio = PowerRatio(battery);
                if (ratio <= bestRatio)
                    continue;

                bestRatio = ratio;
                best = new BatterySource
                {
                    Battery = battery,
                    SourceParent = charger,
                    Kind = "charger"
                };
            }

            if (best != null)
            {
                SwapLog.Write("CHARGER_SPARE", "actor=" + Name(actor)
                    + " charger={" + Item(best.SourceParent) + "}"
                    + " battery={" + Item(best.Battery) + "}");
            }

            return best;
        }

        private static BatterySource ChargerSourceFrom(CondOwner charger, CondOwner tool, CondOwner currentBattery)
        {
            var battery = ChargerBattery(charger);
            if (battery == null)
                return null;

            if (!Compatible(tool, battery))
                return null;

            if (currentBattery != null && !CanAcceptBattery(charger, currentBattery))
                return null;

            if (PowerRatio(battery) <= Math.Max(Plugin.SwapThresholdPercent.Value / 100.0, 0.01))
                return null;

            return new BatterySource
            {
                Battery = battery,
                SourceParent = charger,
                Kind = "charger"
            };
        }

        private static IEnumerable<CondOwner> GetActorShipCOs(CondOwner actor)
        {
            var ship = actor?.ship;
            if (ship == null)
                return new List<CondOwner>();

            try
            {
                return ship.GetCOs(null, bSubObjects: true, bAllowDocked: true, bAllowLocked: true) ?? new List<CondOwner>();
            }
            catch (Exception ex)
            {
                SwapLog.Write("SHIP_SCAN_ERROR", "actor=" + Name(actor) + " error=" + ex.GetType().Name + " " + ex.Message);
                return new List<CondOwner>();
            }
        }

        private static CondOwner ChargerBattery(CondOwner charger)
        {
            if (charger?.objContainer == null)
                return null;

            try
            {
                return charger.objContainer.GetCOs(bAllowLocked: true, objCondTrig: null)
                    .FirstOrDefault(IsBattery);
            }
            catch
            {
                return null;
            }
        }

        private static bool QueueWalkToCharger(CondOwner actor, CondOwner tool, CondOwner currentBattery, BatterySource source, CondOwner originalTarget, Interaction originalInteraction, string reason)
        {
            if (actor?.ship == null || source?.SourceParent == null || string.IsNullOrEmpty(actor.strID))
                return false;

            try
            {
                var usePos = source.SourceParent.GetPos("use");
                var tile = actor.ship.GetTileAtWorldCoords1(usePos.x, usePos.y, bAllowDocked: true);
                if (tile == null)
                {
                    SwapLog.Write("CHARGER_WALK_FAIL", "actor=" + Name(actor)
                        + " charger={" + Item(source.SourceParent) + "}"
                        + " reason=no_tile");
                    return false;
                }

                PendingChargerSwaps[actor.strID] = new PendingChargerSwap
                {
                    ActorId = actor.strID,
                    ToolId = tool.strID,
                    ChargerId = source.SourceParent.strID,
                    OriginalTargetId = originalTarget?.strID,
                    OriginalInteractionName = originalInteraction?.strName,
                    OriginalWasManual = originalInteraction?.bManual ?? true,
                    CreatedUtc = DateTime.UtcNow
                };

                InternalQueueDepth++;
                bool queued;
                try
                {
                    queued = actor.AIIssueOrder(null, null, originalInteraction?.bManual ?? true, tile, usePos.x, usePos.y);
                }
                finally
                {
                    InternalQueueDepth--;
                }

                if (!queued)
                {
                    PendingChargerSwaps.Remove(actor.strID);
                    SwapLog.Write("CHARGER_WALK_FAIL", "actor=" + Name(actor)
                        + " charger={" + Item(source.SourceParent) + "}"
                        + " reason=queue_failed");
                    return false;
                }

                SwapLog.Write("CHARGER_WALK_QUEUED", "actor=" + Name(actor)
                    + " tool={" + Item(tool) + "}"
                    + " old={" + Item(currentBattery) + "}"
                    + " charger={" + Item(source.SourceParent) + "}"
                    + " spare={" + Item(source.Battery) + "}"
                    + " original=" + (originalInteraction?.strName ?? "<null>")
                    + " target={" + Item(originalTarget) + "}"
                    + " reason=" + reason);
                return true;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(actor?.strID))
                    PendingChargerSwaps.Remove(actor.strID);

                SwapLog.Write("CHARGER_WALK_ERROR", "actor=" + Name(actor)
                    + " charger={" + Item(source?.SourceParent) + "}"
                    + " error=" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static void RequeueOriginal(CondOwner actor, PendingChargerSwap pending)
        {
            if (actor == null || pending == null || string.IsNullOrEmpty(pending.OriginalInteractionName))
                return;

            var target = ResolveCO(pending.OriginalTargetId);
            if (target == null)
            {
                SwapLog.Write("CHARGER_REQUEUE_SKIP", "actor=" + Name(actor)
                    + " interaction=" + pending.OriginalInteractionName
                    + " targetId=" + pending.OriginalTargetId
                    + " reason=target_missing");
                return;
            }

            var interaction = DataHandler.GetInteraction(pending.OriginalInteractionName);
            if (interaction == null)
            {
                SwapLog.Write("CHARGER_REQUEUE_SKIP", "actor=" + Name(actor)
                    + " interaction=" + pending.OriginalInteractionName
                    + " reason=interaction_missing");
                return;
            }

            interaction.bManual = pending.OriginalWasManual;

            InternalQueueDepth++;
            try
            {
                if (actor.QueueInteraction(target, interaction))
                {
                    SwapLog.Write("CHARGER_REQUEUE_DONE", "actor=" + Name(actor)
                        + " interaction=" + pending.OriginalInteractionName
                        + " target={" + Item(target) + "}");
                }
                else
                {
                    SwapLog.Write("CHARGER_REQUEUE_FAIL", "actor=" + Name(actor)
                        + " interaction=" + pending.OriginalInteractionName
                        + " target={" + Item(target) + "}");
                }
            }
            catch (Exception ex)
            {
                SwapLog.Write("CHARGER_REQUEUE_ERROR", "actor=" + Name(actor)
                    + " interaction=" + pending.OriginalInteractionName
                    + " error=" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                InternalQueueDepth--;
            }
        }

        private static bool IsNear(CondOwner actor, CondOwner target)
        {
            if (actor == null || target == null)
                return false;

            try
            {
                var usePos = target.GetPos("use");
                var actorPos = actor.transform.position;
                var distance = Vector2.Distance(new Vector2(actorPos.x, actorPos.y), usePos);
                return distance <= Math.Max(0.25, Plugin.ChargerUseRangeTiles.Value);
            }
            catch
            {
                return false;
            }
        }

        private static CondOwner ResolveCO(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            try
            {
                return DataHandler.mapCOs.TryGetValue(id, out var co) ? co : null;
            }
            catch
            {
                return null;
            }
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

        private static bool CanAcceptBattery(CondOwner containerOwner, CondOwner battery)
        {
            if (containerOwner?.objContainer == null || battery == null)
                return false;

            try
            {
                return containerOwner.objContainer.AllowedCO(battery);
            }
            catch
            {
                var allowed = containerOwner.objContainer.ctAllowed?.strName ?? "";
                if (allowed.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                return IsBattery(battery);
            }
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
        private static bool Prefix(CondOwner __instance, CondOwner objTarget, Interaction objInteraction)
        {
            return BatterySwap.TrySwapHeldTools(__instance, objInteraction?.strName ?? "QueueInteraction", objTarget, objInteraction);
        }
    }

    [HarmonyPatch(typeof(CondOwner), "ClearInteraction")]
    internal static class ClearInteractionPatch
    {
        private static void Postfix(CondOwner __instance, Interaction objInteraction)
        {
            BatterySwap.TryCompletePending(__instance, "Clear:" + (objInteraction?.strName ?? "<null>"));
        }
    }
}
