using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: InternalsVisibleTo("BetterTradeBook.Tests")]
[assembly: InternalsVisibleTo("BetterTradeBook.QA")]

namespace BetterTradeBook
{
    // EconomyOverhaul's trade-book features as their own mod (BETTERTRADEBOOK_DRAFT.md): bulk buying and selling, the mass
    // spawning of bought goods, buying and selling from the cart, the trade book from reputation 0; the game's own prices.
    [BepInPlugin(Guid, "Better Trade Book", Version)]
    // EconomyOverhaul has its own copy of these features: BepInEx skips this mod (and logs it) when EconomyOverhaul is installed.
    [BepInIncompatibility("DogEggz.EconomyOverhaul")]
    // Loaded after Dizzy fixes when present, so its trade-book sale prefix can be suspended (TradeSaleCompatibility).
    [BepInDependency(TradeSaleCompatibility.DizzyGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DogEggz.BetterTradeBook";
        public const string Version = "1.0.0";
        internal static ManualLogSource Log;
        private Harmony harmony;
        private void Awake()
        {
            Log = Logger;
            try
            {
                harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(Plugin).Assembly);
                TradeSaleCompatibility.Install(harmony);
                Logger.LogInfo("Better Trade Book " + Version + " loaded.");
            }
            catch (Exception e)
            {
                harmony?.UnpatchSelf();
                TradeSaleCompatibility.Restore();
                Logger.LogError("Better Trade Book could not initialize; no patch kept. " + e);
                enabled = false;
            }
        }
        private IEnumerator Start()
        {
            // The game's Awake order can leave the book's UI unready until every scene Start has run.
            yield return null; yield return null;
            if (EconomyUI.instance) BulkTradeUI.Attach(EconomyUI.instance);
        }
        private void OnDestroy() { harmony?.UnpatchSelf(); TradeSaleCompatibility.Restore(); }
    }
    internal static class Fields
    {
        private static readonly Dictionary<string, FieldInfo> cache = new Dictionary<string, FieldInfo>();
        public static FieldInfo Find(Type type, string name)
        {
            string key = type.FullName + "." + name;
            if (!cache.TryGetValue(key, out FieldInfo f)) cache[key] = f = AccessTools.Field(type, name) ?? throw new MissingFieldException(key);
            return f;
        }
        public static T Get<T>(object obj, string name) => (T)Find(obj.GetType(), name).GetValue(obj);
        public static void Set(object obj, string name, object value) => Find(obj.GetType(), name).SetValue(obj, value);
        public static object Call(object obj, string name, params object[] args) => AccessTools.Method(obj.GetType(), name).Invoke(obj, args);
    }
    // Trade-book errors in the mod's log, at most once a minute per place.
    internal static class BookLog
    {
        private static readonly Dictionary<string, float> last = new Dictionary<string, float>();
        internal static void Error(string where, Exception e)
        {
            float now = Time.realtimeSinceStartup;
            if (last.TryGetValue(where, out float at) && now - at < 60) return;
            last[where] = now;
            Plugin.Log?.LogError("Trade book error while " + where + ": " + e);
        }
    }
    internal static class Notice
    {
        internal static void Show(string text) { if (NotificationUi.instance) NotificationUi.instance.ShowNotification(text); }
    }
    // Dizzy fixes replaces EconomyUI.SellGood with its own prefix; HarmonyX runs both, so a sale would happen twice. Only its
    // overlapping sale prefix is taken off while this mod runs, and put back when it unloads (EconomyOverhaul's).
    internal static class TradeSaleCompatibility
    {
        internal const string DizzyGuid = "com.dizzy.sailwind.fixes";
        private static readonly MethodInfo Sale = AccessTools.Method(typeof(EconomyUI), "SellGood");
        private static Patch suspended;

        internal static void Install(Harmony harmony)
        {
            if (suspended != null) return;
            var patch = Harmony.GetPatchInfo(Sale)?.Prefixes.FirstOrDefault(p =>
                p.owner == DizzyGuid &&
                p.PatchMethod.DeclaringType?.FullName == "Dizzy.Fixes.EconomyUiSellGoodPatch" &&
                p.PatchMethod.Name == "Prefix");
            if (patch == null) return;

            // HarmonyX can run both replacement prefixes even when one returns false.
            // Ordering them cannot prevent duplicate sales or cancellation before repayment.
            // Our sale handler already checks full cargo and settles the exact crate.
            suspended = patch;
            harmony.Unpatch(Sale, patch.PatchMethod);
            Plugin.Log.LogInfo("Dizzy compatibility: Better Trade Book handles trade-book sales; only Dizzy's overlapping sale prefix is suspended.");
        }

        internal static void Restore()
        {
            var patch = suspended;
            if (patch == null) return;
            if (Harmony.GetPatchInfo(Sale)?.Prefixes.Any(p =>
                p.owner == patch.owner && p.PatchMethod == patch.PatchMethod) != true)
            {
                new Harmony(patch.owner).Patch(Sale, prefix: new HarmonyMethod(patch.PatchMethod)
                {
                    priority = patch.priority,
                    before = patch.before,
                    after = patch.after
                });
            }
            suspended = null;
        }
    }
}
