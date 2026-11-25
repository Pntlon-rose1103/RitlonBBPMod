using System.Collections.Generic;
using UnityEngine;

// =====================================================================
// FireWall
//   Mintoが設置する炎の壁。
//   - Fire_0〜Fire_7 を2秒で1周するループアニメーション
//   - Minto と Biten、美天に連行中のプレイヤーは素通り、それ以外（NPC・プレイヤー）はKemonekoのように弾き飛ばす
//   - 設置から20秒で消える（Kagutsuchi の炎は寿命・弾き飛ばしの有無を Spawn() で指定できる）
//   - BSODA / DietBSODA のスプレーが当たると即消滅（消火）
//   - Floodイベント発生中は即消滅（消火）
//   - Fire_OBJ_SE.wav を距離減衰ありでループ再生（Anubisと同じ PropagatedAudioManager）
// =====================================================================
public class FireWall : MonoBehaviour
{
    public const float LIFETIME = 20f;        // 寿命
    public const float ANIM_LOOP = 2f;        // アニメ1周の時間
    public const float PUSH_RADIUS = 7.5f;    // この距離以内に入ったら弾く（1マス=10f → 0.75マス）
    public const float PUSH_SPEED = 20f;      // Kemonekoと同じ
    public const float PUSH_ACCEL = -10f;     // Kemonekoと同じ
    public const float PUSH_COOLDOWN = 0.1f;  // 同じ相手を連続で弾く間隔
    public const bool PUSH_PLAYER = true;     // プレイヤーも弾くか

    public static Sprite[] frames;
    public static SoundObject audLoop;

    private EnvironmentController ec;
    private SpriteRenderer sr;
    private PropagatedAudioManager propAudMan;
    private float age = 0f;
    public bool Extinguished { get; private set; }   // 消火済み（音を鳴らし終えるまでオブジェクトだけ残る）
    private static SoundObject audWater;
    private static bool audWaterSearched = false;
    private float lifetime = LIFETIME;   // この炎の寿命（Spawn で上書き可）
    private bool pushEntities = true;    // 弾き飛ばし効果を持つか
    // BSODA検索のキャッシュ（炎が何枚あっても1フレームに1回だけ探す）
    private static int sodaCacheFrame = -1;
    private static ITM_BSODA[] sodaCache = new ITM_BSODA[0];

    private readonly Dictionary<Entity, float> pushCooldowns = new Dictionary<Entity, float>();

    public static void LoadAssets()
    {
        frames = new Sprite[8];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = SpriteHelper.LoadSprite($"Fire_{i}.png");
        // 字幕なし（FireWallは同時に何枚も出るため）
        audLoop = SpriteHelper.LoadSound("Fire_OBJ_SE.wav", "FireWallLoop");
        MyPlugin.Log.LogInfo("FireWall sprites loaded.");
    }

    // template: 見た目（マテリアル・高さ・大きさ）をコピーする元のSpriteRenderer（Mintoのもの）
    public static FireWall Spawn(EnvironmentController ec, Vector3 pos, SpriteRenderer template)
    {
        return Spawn(ec, pos, template, LIFETIME, true);
    }

    // lifetime: 炎が残る秒数 / pushEntities: false なら見た目だけ（弾き飛ばさない）
    public static FireWall Spawn(EnvironmentController ec, Vector3 pos, SpriteRenderer template, float lifetime, bool pushEntities)
    {
        if (frames == null) LoadAssets();

        GameObject go = new GameObject("FireWall");
        // 非アクティブのまま部品を付け、audioDevice を設定してから有効化する
        // （PropagatedAudioManager の Awake を audioDevice 設定後に走らせるため）
        go.SetActive(false);
        go.transform.position = pos;

        AudioSource src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        PropagatedAudioManager pam = go.AddComponent<PropagatedAudioManager>();
        pam.audioDevice = src;

        GameObject spriteGo = new GameObject("Sprite");
        spriteGo.transform.SetParent(go.transform, false);
        SpriteRenderer renderer = spriteGo.AddComponent<SpriteRenderer>();

        if (template != null)
        {
            // NPCと同じビルボード用マテリアル・高さ・サイズを使う
            renderer.sharedMaterial = template.sharedMaterial;
            spriteGo.transform.position = new Vector3(pos.x, template.transform.position.y, pos.z);
            // Mintoの見た目は2倍なので、炎は元の大きさで固定
            spriteGo.transform.localScale = new Vector3(3.25f, 3.25f, 3.25f);
            spriteGo.layer = template.gameObject.layer;
        }
        else
        {
            spriteGo.transform.localPosition = new Vector3(0f, 5f, 0f);
            spriteGo.transform.localScale = new Vector3(3.25f, 3.25f, 3.25f);
        }
        renderer.sprite = frames[0];

        FireWall fw = go.AddComponent<FireWall>();
        fw.ec = ec;
        fw.sr = renderer;
        fw.propAudMan = pam;
        fw.lifetime = lifetime;
        fw.pushEntities = pushEntities;
        go.SetActive(true);
        fw.PlayLoop();
        MyPlugin.Log.LogInfo("[FireWall] Spawned at " + pos);
        return fw;
    }

    // Anubisと同じパターン: 止まっていたら再生
    private void PlayLoop()
    {
        if (propAudMan == null || audLoop == null) return;
        if (propAudMan.audioSourceManager.isPlaying) return;
        propAudMan.PlaySingle(audLoop);
        if (propAudMan.audioDevice != null) propAudMan.audioDevice.pitch = 1f;
    }

    private void Update()
    {
        if (Extinguished) return;
        float dt = Time.deltaTime * (ec != null ? ec.EnvironmentTimeScale : 1f);
        age += dt;

        if (age >= lifetime)
        {
            Destroy(gameObject);
            return;
        }

        // ── Floodイベント発生中は消火 ─────────────────────
        if (ec != null && ec.GetEvent(RandomEventType.Flood) != null)
        {
            MyPlugin.Log.LogInfo("[FireWall] Extinguished by Flood event at " + transform.position);
            Destroy(gameObject);
            return;
        }

        // ── BSODA / DietBSODA で消火 ─────────────────────
        if (HitBySoda())
        {
            MyPlugin.Log.LogInfo("[FireWall] Extinguished by BSODA at " + transform.position);
            Extinguish();
            return;
        }

        PlayLoop();

        // ── アニメーション ──────────────────────────────
        if (frames != null && sr != null)
        {
            int f = (int)((age % ANIM_LOOP) / ANIM_LOOP * frames.Length) % frames.Length;
            if (frames[f] != null) sr.sprite = frames[f];
        }

        // ── 弾き飛ばしのクールダウン更新 ─────────────────
        if (pushCooldowns.Count > 0)
        {
            List<Entity> keys = new List<Entity>(pushCooldowns.Keys);
            foreach (Entity k in keys)
            {
                pushCooldowns[k] -= dt;
                if (pushCooldowns[k] <= 0f || k == null) pushCooldowns.Remove(k);
            }
        }

        if (ec == null) return;
        if (!pushEntities) return;

        // ── NPCを弾く（Minto・Biten・Kagutsuchiは素通り） ────────────
        foreach (NPC npc in ec.Npcs)
        {
            if (npc == null) continue;
            if (npc is Minto || npc is Biten || npc is Kagutsuchi) continue;
            TryPush(npc.GetComponent<Entity>(), npc.transform.position);
        }

        // ── プレイヤーを弾く ─────────────────────────────
        if (PUSH_PLAYER && ec.Players != null)
        {
            foreach (PlayerManager pm in ec.Players)
            {
                if (pm == null || pm.plm == null) continue;
                if (IsCarriedByBiten(pm)) continue; // 美天に連行中なら素通り
                TryPush(pm.plm.Entity, pm.transform.position);
            }
        }
    }

    // 消火: 炎を消して FloodEvent の水音（audWater）を鳴らし、鳴り終わる頃にオブジェクトを破棄
    public void Extinguish()
    {
        if (Extinguished) return;
        Extinguished = true;
        if (sr != null) sr.enabled = false;
        SoundObject water = GetWaterSound();
        if (propAudMan != null && water != null)
        {
            propAudMan.FlushQueue(true);
            propAudMan.PlaySingle(water);
            Destroy(gameObject, 3f);
        }
        else Destroy(gameObject);
    }

    // FloodEvent（プレハブ）の private な audWater を借りる
    public static SoundObject GetWaterSound()
    {
        if (audWater != null || audWaterSearched) return audWater;
        audWaterSearched = true;
        try
        {
            var fi = HarmonyLib.AccessTools.Field(typeof(FloodEvent), "audWater");
            foreach (FloodEvent fe in Resources.FindObjectsOfTypeAll<FloodEvent>())
            {
                if (fe == null) continue;
                audWater = fi.GetValue(fe) as SoundObject;
                if (audWater != null) break;
            }
        }
        catch (System.Exception e) { MyPlugin.Log.LogWarning("[FireWall] audWater lookup failed: " + e.Message); }
        if (audWater == null) audWaterSearched = false; // 見つからなければ次回また探す
        return audWater;
    }

    // BSODA と DietBSODA はどちらも ITM_BSODA コンポーネントのスプレー
    // （ToyBazookaの弾は ITM_BSODA を外しているので対象外）
    private bool HitBySoda()
    {
        if (Time.frameCount != sodaCacheFrame)
        {
            sodaCacheFrame = Time.frameCount;
            sodaCache = FindObjectsOfType<ITM_BSODA>();
        }
        foreach (ITM_BSODA soda in sodaCache)
        {
            if (soda == null) continue;
            // アイテムの元データ（プレハブ）を除外し、ゲーム中に撃たれたものだけ見る
            if (soda.gameObject.scene != gameObject.scene) continue;
            Vector3 diff = soda.transform.position - transform.position;
            diff.y = 0f;
            if (diff.magnitude < PUSH_RADIUS) return true;
        }
        return false;
    }

    // 美天に連行されているプレイヤーかどうか
    private bool IsCarriedByBiten(PlayerManager pm)
    {
        foreach (NPC npc in ec.Npcs)
        {
            Biten b = npc as Biten;
            if (b != null && b.isDetaining && b.capturedPlayer == pm) return true;
        }
        return false;
    }

    private void TryPush(Entity ent, Vector3 targetPos)
    {
        if (ent == null) return;
        if (pushCooldowns.ContainsKey(ent)) return;

        // 高さは無視して水平距離で判定
        Vector3 diff = targetPos - transform.position;
        diff.y = 0f;
        if (diff.magnitude >= PUSH_RADIUS) return;

        Vector3 dir = diff.sqrMagnitude > 0.0001f ? diff.normalized : Vector3.forward;
        ent.AddForce(new Force(dir, PUSH_SPEED, PUSH_ACCEL));
        pushCooldowns[ent] = PUSH_COOLDOWN;
    }
}
