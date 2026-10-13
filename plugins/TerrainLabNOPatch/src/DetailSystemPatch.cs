using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Effects;
using UnityEngine;

namespace TerrainLab.NOPatch
{
    /// <summary>
    /// Patches 4 and 2. Stock maps carry one GameObject "DetailRenderer_Base" (direct child of the map root) with
    /// ShaderGlobalManager, DetailRenderer, GrassRenderer, TreeRenderer, TerrainHeightMap, BlastManager and
    /// DetailRendererDebugger. Without it, TerrainHeightMapBlocker.Start (SceneSingleton&lt;TerrainHeightMap&gt;.i
    /// .RegisterObject) and blasts (SceneSingleton&lt;BlastManager&gt;.i.AddBlast) hit a null or stale singleton, and
    /// _Datum_WorldExtent is never set. We clone the stock object while inactive, point every map reference at our
    /// map, swap the tree data for ours, and only then activate it.
    /// </summary>
    static class DetailSystemPatch
    {
        internal static GameObject Clone;

        internal static void Apply(MapContext ctx)
        {
            var stockMap = Stock.MapPrefab();
            if (stockMap == null) { Plugin.Log.LogError("[4] No stock map prefab; detail system not added."); return; }
            var stockDR = stockMap.GetComponentInChildren<DetailRenderer>(true);
            if (stockDR == null) { Plugin.Log.LogError($"[4] No DetailRenderer under stock '{stockMap.name}'."); return; }
            var stockGO = stockDR.gameObject;
            Plugin.Log.LogInfo($"[4] Stock detail object '{Path(stockGO.transform)}' components [{string.Join(", ", stockGO.GetComponents<Component>().Select(c => c.GetType().Name))}]");

            if (ctx.Root.GetComponentInChildren<DetailRenderer>(true) != null)
                Plugin.Log.LogWarning("[4] Our map already has a DetailRenderer; adding a second one anyway (check the bundle).");

            // Clone under an inactive holder so no Awake/OnEnable runs before rewiring.
            var holder = new GameObject("TerrainLabDetailHolder (inactive)");
            holder.SetActive(false);
            holder.transform.SetParent(ctx.Root, false);
            var clone = UnityEngine.Object.Instantiate(stockGO, holder.transform, false);
            clone.name = "DetailRenderer_Base (TerrainLab)";
            clone.transform.localPosition = stockGO.transform.localPosition;
            clone.transform.localRotation = stockGO.transform.localRotation;

            foreach (var dbg in clone.GetComponents<DetailRendererDebugger>())
            {
                UnityEngine.Object.DestroyImmediate(dbg);
                Plugin.Log.LogInfo("[4] Removed DetailRendererDebugger (its OnEnable swaps materials).");
            }

            var dr = clone.GetComponent<DetailRenderer>();
            var thm = clone.GetComponent<TerrainHeightMap>();
            var blast = clone.GetComponent<BlastManager>();
            var sgm = clone.GetComponent<ShaderGlobalManager>();

            // TerrainHeightMap
            if (thm != null)
            {
                Set(thm, "mapSettings", ctx.Map);
                thm.AutoSearchRoot = ctx.Root;
                thm.AutoFindTerrain = true;
                Set(thm, "manualHeightProvider", new MeshRenderer[0]);
                Set(thm, "manualHeightBlocker", new MeshRenderer[0]);
                var phys = new List<PhysicMaterial>(thm.terrainPhysicMaterials ?? new PhysicMaterial[0]);
                var gaTerrain = GameAssets.i != null ? GameAssets.i.terrainMaterial : null;
                if (gaTerrain != null && !phys.Contains(gaTerrain)) phys.Add(gaTerrain);
                foreach (var pm in ctx.OriginalTilePhysicMaterials) if (!phys.Contains(pm)) phys.Add(pm);
                thm.terrainPhysicMaterials = phys.ToArray();
                var mats = new List<Material>((Material[])Get(thm, "terrainMaterials") ?? new Material[0]);
                foreach (var mr in ctx.Tiles) foreach (var m in mr.sharedMaterials) if (m != null && !mats.Contains(m)) mats.Add(m);
                Set(thm, "terrainMaterials", mats.ToArray());
                thm.height = new MinMax(Plugin.HeightMin.Value, Plugin.HeightMax.Value);
                Plugin.Log.LogInfo($"[4] TerrainHeightMap: mapSettings=ours, AutoSearchRoot='{ctx.Root.name}', AutoFindTerrain=true, AutoFindBlockers={thm.AutoFindBlockers}, " +
                                   $"terrainPhysicMaterials=[{string.Join(", ", phys.Select(p => p.name))}], terrainMaterials=[{string.Join(", ", mats.Select(m => m.name))}], " +
                                   $"height={thm.height.Min}..{thm.height.Max}, heightBakeShader={Name(Get(thm, "heightBakeShader"))}, blockerShader={Name(Get(thm, "blockerShader"))}");
            }
            else Plugin.Log.LogWarning("[4] Clone has no TerrainHeightMap.");

            if (blast != null) { blast.mapSettings = ctx.Map; Plugin.Log.LogInfo("[4] BlastManager.mapSettings = ours"); }
            else Plugin.Log.LogWarning("[4] Clone has no BlastManager.");
            if (sgm != null) { Set(sgm, "mapSettings", ctx.Map); Plugin.Log.LogInfo($"[4] ShaderGlobalManager.mapSettings = ours (_Datum_WorldExtent will be {ctx.Map.MapSize / 2f})"); }
            else Plugin.Log.LogWarning("[4] Clone has no ShaderGlobalManager.");

            // Grass. Keep the GrassRenderer in the array: DetailRenderer.Start sizes its arg buffer from the counts.
            foreach (var g in clone.GetComponents<GrassRenderer>())
            {
                switch (Plugin.Grass.Value)
                {
                    case GrassMode.Disable:
                        g.EnabledSetting = DetailEnabled.Disable;
                        Plugin.Log.LogInfo("[4] GrassRenderer.EnabledSetting = Disable");
                        break;
                    case GrassMode.OurMask:
                        var mask = OurAssets.Get<Texture2D>(Plugin.GrassMaskAsset.Value);
                        var lush = (Texture2D[])Get(g, "lushMaps");
                        if (mask != null && lush != null)
                        {
                            for (int i = 0; i < lush.Length; i++) lush[i] = mask;
                            Set(g, "lushMaps", lush);
                            Plugin.Log.LogInfo($"[4] Grass uses our '{mask.name}' {mask.width}x{mask.height} for all {lush.Length} lush maps");
                        }
                        else { g.EnabledSetting = DetailEnabled.Disable; Plugin.Log.LogWarning("[4] GrassMode=OurMask but no GrassMask; grass disabled."); }
                        break;
                    default:
                        Plugin.Log.LogWarning("[4] GrassMode=Stock: Heartland lush maps drive grass on our terrain (test only).");
                        break;
                }
            }

            // Trees (patch 2).
            TreeSetup(ctx, clone, dr);

            // Anything still pointing into the stock prefab hierarchy is a bug: report it.
            ScanStockRefs(clone, stockMap.transform);

            clone.transform.SetParent(ctx.Root, true);   // activates the clone: Awake/OnEnable run now, Start next frame
            UnityEngine.Object.Destroy(holder);
            Clone = clone;
            Plugin.Log.LogInfo($"[4] Detail system active: DetailRenderer.i={(DetailRenderer.i == dr ? "clone" : "OTHER")}, TerrainHeightMap.i={(SceneSingleton<TerrainHeightMap>.i == thm ? "clone" : "OTHER")}, " +
                               $"BlastManager.i={(SceneSingleton<BlastManager>.i == blast ? "clone" : "OTHER")}, headless={GameManager.IsHeadless}, activeInHierarchy={clone.activeInHierarchy}");
        }

        static void TreeSetup(MapContext ctx, GameObject clone, DetailRenderer dr)
        {
            var cloneTrees = clone.GetComponents<TreeRenderer>();
            var stockTree = cloneTrees.FirstOrDefault();
            // A TreeRenderer stub on our own prefab (importer AddTreeRendererStub) wins over the clone's.
            var stub = ctx.Root.GetComponentsInChildren<TreeRenderer>(true).FirstOrDefault(t => t.gameObject != clone && !t.transform.IsChildOf(clone.transform));

            TreeRenderer use = null;
            if (!Plugin.TreesEnabled.Value) Plugin.Log.LogInfo("[2] Trees disabled in config.");
            else
            {
                var data = stub != null && stub.PositionData != null ? stub.PositionData : OurAssets.Get<TextAsset>(Plugin.TreePositionsAsset.Value);
                if (data == null) Plugin.Log.LogWarning($"[2] No '{Plugin.TreePositionsAsset.Value}' TextAsset in our bundle; no trees.");
                else if (data.bytes.Length % 12 != 0) Plugin.Log.LogWarning($"[2] '{data.name}' is {data.bytes.Length} bytes, not a multiple of 12 (float3); no trees.");
                else if (stockTree == null && stub == null) Plugin.Log.LogWarning("[2] Stock detail object has no TreeRenderer; no trees.");
                else
                {
                    use = stub != null ? stub : stockTree;
                    if (stub != null && stockTree != null)
                    {
                        foreach (var f in new[] { "mesh", "material", "treeColorCompute", "clearShader" })
                            if (Get(stub, f) as UnityEngine.Object == null) Set(stub, f, Get(stockTree, f));
                        if ((float)Get(stub, "viewRadius") <= 0f) Set(stub, "viewRadius", Get(stockTree, "viewRadius"));
                        Plugin.Log.LogInfo("[2] Using our prefab's TreeRenderer stub; filled missing fields from stock.");
                    }
                    use.PositionData = data;
                    var lush = OurAssets.Get<Texture2D>(Plugin.LushMapAsset.Value);
                    use.LushMap = lush != null ? lush : Texture2D.whiteTexture;
                    if (Plugin.TreeAltitudeMin.Value >= 0f) use.AltitudeRange.Min = Plugin.TreeAltitudeMin.Value;
                    if (Plugin.TreeAltitudeMax.Value >= 0f) use.AltitudeRange.Max = Plugin.TreeAltitudeMax.Value;
                    var mesh = Get(use, "mesh") as Mesh; var mat = Get(use, "material") as Material;
                    Plugin.Log.LogInfo($"[2] TreeRenderer: PositionData '{data.name}' = {data.bytes.Length / 12} trees, LushMap '{use.LushMap.name}', " +
                                       $"mesh {Name(mesh)}, material {Name(mat)} ({mat?.shader?.name}), treeColorCompute {Name(Get(use, "treeColorCompute"))}, clearShader {Name(Get(use, "clearShader"))}, " +
                                       $"viewRadius {Get(use, "viewRadius")}, meshRadius {Get(use, "meshRadius")}, AltitudeRange {use.AltitudeRange.Min}..{use.AltitudeRange.Max}");
                    if (Plugin.DumpTreeMaterial.Value && !treeDumped && mat != null) { treeDumped = true; TerrainShaderPatch.Dump(mat, "[2] Stock tree material"); }
                    try { TreeLook.Apply(use, mat); }
                    catch (Exception e) { Plugin.Log.LogError($"[2b] Tree look failed, stock tree look kept: {e}"); Set(use, "mesh", mesh); Set(use, "material", mat); }
                    mesh = Get(use, "mesh") as Mesh; mat = Get(use, "material") as Material;
                    if (mesh == null || mat == null || Get(use, "treeColorCompute") == null) { Plugin.Log.LogError("[2] TreeRenderer still lacks mesh/material/compute; no trees."); use = null; }
                }
            }

            // Never leave Heartland PositionData in the clone.
            foreach (var t in cloneTrees)
                if (t != use) { UnityEngine.Object.DestroyImmediate(t); }
            if (stub != null && stub != use) stub.enabled = false;
            Set(dr, "treeRenderers", use != null ? new[] { use } : new TreeRenderer[0]);
            Plugin.Log.LogInfo($"[2] DetailRenderer.treeRenderers = {(use != null ? "[ours]" : "[] (no trees)")}");
        }

        static bool treeDumped;

        static void ScanStockRefs(GameObject clone, Transform stockRoot)
        {
            int bad = 0;
            foreach (var comp in clone.GetComponents<Component>())
            {
                if (comp == null) continue;
                for (var type = comp.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(Component); type = type.BaseType)
                {
                    foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        object v;
                        try { v = f.GetValue(comp); } catch { continue; }
                        foreach (var o in Flatten(v))
                        {
                            Transform tr = o is Component c ? (c != null ? c.transform : null) : o is GameObject g ? (g != null ? g.transform : null) : null;
                            if (tr != null && tr.IsChildOf(stockRoot))
                            {
                                bad++;
                                Plugin.Log.LogWarning($"[4] {type.Name}.{f.Name} still points at stock '{Path(tr)}'");
                            }
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"[4] Stock-reference scan: {bad} leftover reference(s).");
        }

        static IEnumerable<UnityEngine.Object> Flatten(object v)
        {
            if (v is UnityEngine.Object o) { yield return o; yield break; }
            if (v is IEnumerable e && !(v is string))
                foreach (var x in e) if (x is UnityEngine.Object ox) yield return ox;
        }

        internal static object Get(object o, string field)
        {
            var f = AccessTools.Field(o.GetType(), field);
            if (f == null) { Plugin.Log.LogWarning($"Field {o.GetType().Name}.{field} not found (game update?)"); return null; }
            return f.GetValue(o);
        }

        internal static void Set(object o, string field, object value)
        {
            var f = AccessTools.Field(o.GetType(), field);
            if (f == null) { Plugin.Log.LogWarning($"Field {o.GetType().Name}.{field} not found (game update?); not set"); return; }
            f.SetValue(o, value);
        }

        static string Name(object o) => o is UnityEngine.Object u && u != null ? $"'{u.name}'" : "null";

        internal static string Path(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
