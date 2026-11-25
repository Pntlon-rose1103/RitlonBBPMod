using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

// Level Studio エディタ内の SE / BGM 音量。
// Editor が入っている時だけ Install される（Editor なしの環境では何も起きない）。
// 値は OPTIONS > Audio の追加バー（Editor SE / Editor BGM）と BepInEx Config の両方から変更できる。
static class EditorVolume
{
    const int STEPS = 10;
    const float ROW_SCALE = 0.6f;
    static ConfigEntry<int> cfgSe, cfgBgm;
    static FieldInfo fiAudMan, fiLoopMan;
    static readonly FieldInfo fiMult = AccessTools.Field(typeof(AudioManager), "volumeMultiplier");
    static readonly FieldInfo fiBars = AccessTools.Field(typeof(AdjustmentBars), "bars");

    public static void Install(Harmony harmony, Type tEditorController)
    {
        var cfg = MyPlugin.Instance.Config;
        cfgSe = cfg.Bind("EditorAudio", "EditorSE", 5, new ConfigDescription("Level Studio エディタ内の SE 音量 (0-10)", new AcceptableValueRange<int>(0, STEPS)));
        cfgBgm = cfg.Bind("EditorAudio", "EditorBGM", 5, new ConfigDescription("Level Studio エディタ内の BGM 音量 (0-10)", new AcceptableValueRange<int>(0, STEPS)));

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (tEditorController != null)
        {
            fiAudMan = tEditorController.GetField("audMan", all);
            fiLoopMan = tEditorController.GetField("loopingAudMan", all);
            MethodInfo upd = tEditorController.GetMethod("Update", all | BindingFlags.DeclaredOnly);
            if (upd != null)
            {
                harmony.Patch(upd, postfix: new HarmonyMethod(typeof(EditorVolume), nameof(EditorUpdatePostfix)));
                MyPlugin.Log.LogInfo("[LevelStudio Editor] Patched EditorController.Update (volume).");
            }
            else MyPlugin.Log.LogWarning("[LevelStudio Editor] EditorController.Update not found. Editor volume skipped.");
        }

        MethodInfo awake = AccessTools.Method(typeof(OptionsMenu), "Awake");
        if (awake != null)
            harmony.Patch(awake, postfix: new HarmonyMethod(typeof(EditorVolume), nameof(OptionsPostfix)));
    }

    static void EditorUpdatePostfix(object __instance)
    {
        try
        {
            float se = cfgSe.Value / (float)STEPS;
            foreach (var fi in new[] { fiAudMan, fiLoopMan })
            {
                var am = fi?.GetValue(__instance) as AudioManager;
                if (am != null) fiMult.SetValue(am, se);
            }

            var mm = Singleton<MusicManager>.Instance;
            if (mm != null && mm.MidiPlaying)
            {
                float bgm = cfgBgm.Value / (float)STEPS;
                mm.SetMidiVolume(bgm);
            }
        }
        catch (Exception e) { MyPlugin.Log.LogWarning("[EditorVolume] " + e.Message); }
    }

    // ---- OPTIONS > Audio にバーを2本追加 ----
    static Transform RowOf(Component bar, Component other)
    {
        Transform t = bar.transform;
        while (t.parent != null && !other.transform.IsChildOf(t.parent)) t = t.parent;
        return t;
    }

    // バーと同じ親の子から、いちばん近い高さにある TextMeshPro（行ラベル）を探す
    static Transform FindLabel(Transform barRow)
    {
        Transform parent = barRow.parent;
        Transform best = null; float bestDy = float.MaxValue;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform c = parent.GetChild(i);
            if (c == barRow) continue;
            bool isText = c.GetComponentsInChildren<Component>(true).Any(x => x != null && x.GetType().Name.StartsWith("TextMeshPro"));
            if (!isText) continue;
            if (c.localPosition.x >= barRow.localPosition.x) continue; // ラベルはバーの左
            float dy = Mathf.Abs(c.localPosition.y - barRow.localPosition.y);
            if (dy < bestDy) { bestDy = dy; best = c; }
        }
        return best != null && bestDy < 40f ? best : null;
    }

    static void SetText(GameObject go, string label)
    {
        foreach (var tl in go.GetComponentsInChildren<TextLocalizer>(true))
            UnityEngine.Object.DestroyImmediate(tl);
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null || !c.GetType().Name.StartsWith("TextMeshPro")) continue;
            var pi = c.GetType().GetProperty("text");
            if (pi != null) { pi.SetValue(c, label, null); break; }
        }
    }

    static void OptionsPostfix(OptionsMenu __instance)
    {
        try
        {
            var bars = AccessTools.Field(typeof(OptionsMenu), "soundAdj").GetValue(__instance) as AdjustmentBars[];
            if (bars == null || bars.Length < 3) return;

            Transform row0 = RowOf(bars[0], bars[1]);
            Transform row1 = RowOf(bars[1], bars[2]);
            Transform row2 = RowOf(bars[2], bars[1]);
            Transform lab0 = FindLabel(row0), lab1 = FindLabel(row1), lab2 = FindLabel(row2);
            // bars[1]=Effects(上) と bars[2]=Music(下) は2行ぶん離れているので半分が1行
            Vector3 stepDown = (row2.localPosition - row1.localPosition) * 0.5f;
            if (stepDown.y > 0) stepDown = -stepDown;

            Transform rowSe = MakeRow(__instance, row2, "Editor SE", cfgSe);
            Transform rowBgm = MakeRow(__instance, row2, "Editor BGM", cfgBgm);
            if (rowSe == null || rowBgm == null) return;
            Transform labSe = lab2 != null ? CloneLabel(lab2, "Editor SE") : null;
            Transform labBgm = lab2 != null ? CloneLabel(lab2, "Editor BGM") : null;

            // 行 = (バー, ラベル)。上から Y 順に並べ直し、縮小して詰める
            var units = new[]
            {
                new[] { row0, lab0 }, new[] { row1, lab1 }, new[] { row2, lab2 },
                new[] { rowSe, labSe }, new[] { rowBgm, labBgm },
            };
            // 元の順（見た目の上から）：Effects(row1), Voices(row0), Music(row2), SE, BGM
            units = new[] { units[1], units[0], units[2], units[3], units[4] };

            Vector3 top = units[0][0].localPosition - stepDown * 0.25f;
            for (int i = 0; i < units.Length; i++)
            {
                Vector3 target = top + stepDown * (0.55f * i);
                Transform bar = units[i][0], lab = units[i][1];
                Vector3 delta = target - bar.localPosition;
                bar.localPosition = target;
                bar.localScale = bar.localScale * ROW_SCALE;
                if (lab != null)
                {
                    lab.localPosition = new Vector3(lab.localPosition.x, lab.localPosition.y + delta.y, lab.localPosition.z);
                    lab.localScale = lab.localScale * ROW_SCALE;
                }
            }
        }
        catch (Exception e) { MyPlugin.Log.LogWarning("[EditorVolume] options row failed: " + e); }
    }

    static Transform CloneLabel(Transform template, string label)
    {
        GameObject go = UnityEngine.Object.Instantiate(template.gameObject, template.parent);
        go.name = "ModLabel_" + label.Replace(" ", "");
        SetText(go, label);
        return go.transform;
    }

    static Transform MakeRow(OptionsMenu menu, Transform template, string label, ConfigEntry<int> entry)
    {
        GameObject go = UnityEngine.Object.Instantiate(template.gameObject, template.parent);
        go.name = "ModRow_" + label.Replace(" ", "");

        var adj = go.GetComponentInChildren<AdjustmentBars>(true);
        if (adj == null) { UnityEngine.Object.Destroy(go); return null; }

        // 複製された永続リスナー(VolumeChanged(2))を捨てて、自前のものに差し替える
        adj.onValueChanged = new UnityEvent();
        var fiVal = AccessTools.Field(typeof(AdjustmentBars), "val");
        fiVal.SetValue(adj, Mathf.Clamp(entry.Value, 0, ((Array)fiBars.GetValue(adj)).Length));
        AccessTools.Method(typeof(AdjustmentBars), "UpdateBars").Invoke(adj, null);

        adj.onValueChanged.AddListener(() =>
        {
            int v = (int)fiVal.GetValue(adj);
            entry.Value = Mathf.Clamp(v, 0, STEPS);
            MyPlugin.Instance.Config.Save();
            // 確認用に効果音を鳴らす（Effects テスト音）
            try
            {
                var audTest = AccessTools.Field(typeof(OptionsMenu), "audEffectTest").GetValue(menu) as SoundObject;
                var am = AccessTools.Field(typeof(OptionsMenu), "audMan").GetValue(menu) as AudioManager;
                if (am != null && audTest != null) { am.FlushQueue(true); am.QueueAudio(audTest); }
            }
            catch { }
        });
        return go.transform;
    }
}
