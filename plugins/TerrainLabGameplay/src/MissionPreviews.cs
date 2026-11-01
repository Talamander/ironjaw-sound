using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace TerrainLab.Gameplay
{
    /// <summary>
    /// Shows our own preview image for Ironjaw Sound missions in the mission picker.
    ///
    /// MissionsPicker asks MissionKey.GetPreview for the picture. Missions in the user folder go through
    /// MissionGroup.UserGroup.GetPreview, which always returns null, so the picker falls back to its stock default
    /// image (seen in game 2026-10-23: no way to tell the missions apart). For keys starting with "Ironjaw Sound - "
    /// this patch loads preview.jpg from the mission's folder (the installer copies it there), or from the release's
    /// own Missions folder next to the DLL when the player kept an edited copy without one. Other missions are left alone.
    /// </summary>
    static class MissionPreviews
    {
        const string Prefix = "Ironjaw Sound - ";
        const string FileName = "preview.jpg";
        static string pluginDir;
        static readonly Dictionary<string, (DateTime stamp, Sprite sprite)> cache =
            new Dictionary<string, (DateTime, Sprite)>(StringComparer.OrdinalIgnoreCase);

        public static void Patch(Harmony h)
        {
            pluginDir = Path.GetDirectoryName(typeof(MissionPreviews).Assembly.Location);
            var m = AccessTools.Method(typeof(MissionGroup.UserGroup), nameof(MissionGroup.UserGroup.GetPreview));
            if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(MissionPreviews), nameof(GetPreviewPrefix)));
            else Plugin.Log.LogWarning("[TLGameplay] mission previews: UserGroup.GetPreview not found");
        }

        static bool GetPreviewPrefix(string key, ref UniTask<Sprite> __result)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, StringComparison.Ordinal)) return true;
                var sprite = Load(Path.Combine(MissionGroup.UserGroup.GetFolder(key), FileName))
                             ?? (pluginDir == null ? null : Load(Path.Combine(pluginDir, "Missions", key, FileName)));
                if (sprite == null) return true;
                __result = UniTask.FromResult(sprite);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[TLGameplay] mission preview for '{key}' failed: {e.Message}");
                return true;
            }
        }

        static Sprite Load(string path)
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (cache.TryGetValue(path, out var c) && c.stamp == stamp && c.sprite != null) return c.sprite;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "TL mission preview" };
            if (!tex.LoadImage(File.ReadAllBytes(path), true))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = Path.GetFileName(Path.GetDirectoryName(path));
            cache[path] = (stamp, sprite);
            return sprite;
        }
    }
}
