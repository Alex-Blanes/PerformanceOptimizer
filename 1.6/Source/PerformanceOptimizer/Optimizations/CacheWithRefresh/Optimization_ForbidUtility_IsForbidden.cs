using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Verse;
using Verse.AI;

namespace PerformanceOptimizer
{
    public class Optimization_ForbidUtility_IsForbidden : Optimization_RefreshRate
    {
        public static int refreshRateStatic;

        // now: pawnId -> (thingId -> CachedValueTick<bool>)
        public static Dictionary<int, Dictionary<int, CachedValueTick<bool>>> cachedResults = new();
        public override int RefreshRateByDefault => 30;
        public override OptimizationType OptimizationType => OptimizationType.CacheWithRefreshRate;
        public override string Label => "PO.IsForbidden".Translate();
        public override void DoPatches()
        {
            base.DoPatches();
            // patch CheckCurrentToilEndOrFail with a finalizer to ensure the counter is decremented even if exceptions occur
            var method = AccessTools.Method(typeof(JobDriver), "CheckCurrentToilEndOrFail");
            PerformanceOptimizerMod.harmony.Patch(method,
                prefix: new HarmonyMethod(GetMethod(nameof(CheckCurrentToilEndOrFailPrefix))),
                postfix: new HarmonyMethod(GetMethod(nameof(CheckCurrentToilEndOrFailPostfix))),
                finalizer: new HarmonyMethod(GetMethod(nameof(CheckCurrentToilEndOrFailFinalizer))));

            // register in patchedMethods for UnPatchAll
            patchedMethods ??= new Dictionary<MethodBase, List<System.Reflection.MethodInfo>>();
            patchedMethods[method] = new List<System.Reflection.MethodInfo> { GetMethod(nameof(CheckCurrentToilEndOrFailPrefix)), GetMethod(nameof(CheckCurrentToilEndOrFailPostfix)), GetMethod(nameof(CheckCurrentToilEndOrFailFinalizer)) };

            System.Reflection.MethodInfo forbiddedMethod = AccessTools.Method(typeof(ForbidUtility), "IsForbidden", new Type[] { typeof(Thing), typeof(Pawn) });
            Patch(forbiddedMethod, GetMethod(nameof(Prefix)), GetMethod(nameof(Postfix)));
        }

        // Use an atomic counter to support nested calls and avoid getting stuck true
        public static int checkCurrentToilEndOrFailCounter;
        public static void CheckCurrentToilEndOrFailPrefix()
        {
            Interlocked.Increment(ref checkCurrentToilEndOrFailCounter);
        }

        public static void CheckCurrentToilEndOrFailPostfix()
        {
            int val = Interlocked.Decrement(ref checkCurrentToilEndOrFailCounter);
            if (val < 0)
            {
                // protect in case of imbalance
                Interlocked.Exchange(ref checkCurrentToilEndOrFailCounter, 0);
            }
        }

        // finalizer ensuring decrement on exception
        public static Exception CheckCurrentToilEndOrFailFinalizer(Exception __exception)
        {
            int val = Interlocked.Decrement(ref checkCurrentToilEndOrFailCounter);
            if (val < 0)
            {
                Interlocked.Exchange(ref checkCurrentToilEndOrFailCounter, 0);
            }
            return __exception;
        }

        [HarmonyPriority(int.MaxValue)]
        public static bool Prefix(Thing t, Pawn pawn, out CachedValueTick<bool> __state, ref bool __result)
        {
            // Avoid any access if pawn or thing are null - some mods may call with null
            if (pawn == null || t == null)
            {
                __state = null;
                return true;
            }

            // read the counter atomically
            if (Volatile.Read(ref checkCurrentToilEndOrFailCounter) > 0)
            {
                if (!cachedResults.TryGetValue(pawn.thingIDNumber, out Dictionary<int, CachedValueTick<bool>> cachedResult))
                {
                    cachedResults[pawn.thingIDNumber] = cachedResult = new Dictionary<int, CachedValueTick<bool>>();
                }
                if (!cachedResult.TryGetValue(t.thingIDNumber, out __state))
                {
                    cachedResult[t.thingIDNumber] = __state = new CachedValueTick<bool>();
                }
                return __state.SetOrRefresh(ref __result);
            }
            else
            {
                __state = null;
                return true;
            }
        }

        [HarmonyPriority(int.MinValue)]
        public static void Postfix(CachedValueTick<bool> __state, ref bool __result)
        {
            __state?.ProcessResult(ref __result, refreshRateStatic);
        }

        public override void Clear()
        {
            cachedResults.Clear();
        }
    }
}
