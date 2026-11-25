using System.Collections;
using System.Reflection;
using BepInEx;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using UnityEngine;

public class ITM_ToyBazooka : Item
{
    private EnvironmentController ec;
    private PlayerManager player;
    private static SoundObject portalSound = null;
    private static GameObject bazookaProjPrefab = null;
    const float SQUISH_DURATION = 15f;

    public override bool Use(PlayerManager pm)
    {
        player = pm;
        ec = pm.ec;

        if (portalSound == null)
        {
            foreach (var io in Resources.FindObjectsOfTypeAll<ItemObject>())
            {
                if (io.itemType == Items.PortalPoster && io.item != null)
                {
                    ITM_PortalPoster pp = io.item as ITM_PortalPoster;
                    if (pp != null)
                        portalSound = (SoundObject)typeof(ITM_PortalPoster)
                            .GetField("audYes",
                                BindingFlags.NonPublic | BindingFlags.Instance)
                            .GetValue(pp);
                    break;
                }
            }
        }

        if (bazookaProjPrefab == null)
            bazookaProjPrefab = CreateProjPrefab();

        FireOne(pm);
        pm.itm.Remove(0);
        Destroy(gameObject);
        return true;
    }

    void FireOne(PlayerManager pm)
    {
        if (portalSound != null)
            Singleton<CoreGameManager>.Instance.audMan.PlaySingle(portalSound);

        if (bazookaProjPrefab == null) return;

        GameObject go = Instantiate(bazookaProjPrefab);
        go.transform.position = pm.transform.position;
        go.transform.forward =
            Singleton<CoreGameManager>.Instance
                .GetCamera(pm.playerNumber).transform.forward;
        go.SetActive(true);

        ToyBazookaProjectile proj = go.GetComponent<ToyBazookaProjectile>();
        proj.Launch(pm.ec, go.transform.forward, SQUISH_DURATION);
    }

    static GameObject CreateProjPrefab()
    {
        ItemObject bsodaItem = null;
        foreach (var io in Resources.FindObjectsOfTypeAll<ItemObject>())
            if (io.itemType == Items.Bsoda) { bsodaItem = io; break; }
        if (bsodaItem == null) return null;

        bsodaItem.item.gameObject.SetActive(false);
        GameObject go = Instantiate(bsodaItem.item.gameObject);
        bsodaItem.item.gameObject.SetActive(true);

        go.name = "ToyBazookaProj_Prefab";
        DontDestroyOnLoad(go);
        Destroy(go.GetComponent<ITM_BSODA>());
        go.AddComponent<ToyBazookaProjectile>();
        go.SetActive(false);
        return go;
    }

    public static void Register()
    {
        Sprite sprite = SpriteHelper.LoadSprite("toybazooka.png");

        GameObject prefabGo = new GameObject("ToyBazooka_Item");
        DontDestroyOnLoad(prefabGo);
        ITM_ToyBazooka itemComp = prefabGo.AddComponent<ITM_ToyBazooka>();

        ItemObject item = ScriptableObject.CreateInstance<ItemObject>();
        item.name = "ToyBazooka";
        item.itemType = EnumExtensions.ExtendEnum<Items>("ToyBazooka");
        item.item = itemComp;
        item.itemSpriteSmall = sprite;
        item.itemSpriteLarge = sprite;
        item.price = 450;
        item.addToInventory = true;
        item.nameKey = "ToyBazooka";
        item.descKey = "ToyBazooka_desc";

        ItemMetaData meta = new ItemMetaData(MyPlugin.Instance.Info, item);
        meta.flags = ItemFlags.None;
        ItemMetaStorage.Instance.Add(meta);

        // ローカライズ登録は LocalizationPatch.cs（LoadLocalizedText の Postfix）側で
        // 一括して行っている。LoadLocalizedText は辞書を丸ごと作り直すため、
        // ここで直接 Add しても意味がない（Start() 実行時に消される）。

        MyPlugin.Log.LogInfo("ToyBazooka registered.");
    }
}

public class ToyBazookaProjectile : MonoBehaviour, IEntityTrigger
{
    private EnvironmentController ec;
    private Vector3 direction;
    private float speed = 30f;
    private float squishDuration;
    private Entity entity;
    private float lifetime = 8f;
    private bool flying = false;

    public static SoundObject SquishSound = null;

    public static IEnumerator LoadSquishSoundCoroutine()
    {
        if (SquishSound != null) yield break;

        string path = System.IO.Path.Combine(
            Paths.PluginPath, "RitlonBBPMod", "textures", "BalderCrush.wav");

        if (!System.IO.File.Exists(path))
        {
            MyPlugin.Log.LogError($"BalderCrush.wav not found: {path}");
            yield break;
        }

        AudioClip clip = LoadWav(path);
        if (clip == null)
        {
            MyPlugin.Log.LogError("Failed to parse BalderCrush.wav");
            yield break;
        }

        SquishSound = ScriptableObject.CreateInstance<SoundObject>();
        SquishSound.name = "BalderCrush";
        SquishSound.soundClip = clip;
        SquishSound.subtitle = false;

        MyPlugin.Log.LogInfo("BalderCrush.wav loaded successfully!");
        yield break;
    }

    private static AudioClip LoadWav(string path)
    {
        byte[] data = System.IO.File.ReadAllBytes(path);

        int channels = System.BitConverter.ToInt16(data, 22);
        int sampleRate = System.BitConverter.ToInt32(data, 24);
        int bitDepth = System.BitConverter.ToInt16(data, 34);

        int dataIdx = 12;
        while (dataIdx < data.Length - 4)
        {
            if (data[dataIdx] == 'd' && data[dataIdx + 1] == 'a' &&
                data[dataIdx + 2] == 't' && data[dataIdx + 3] == 'a')
            {
                dataIdx += 8;
                break;
            }
            dataIdx++;
        }

        int sampleCount = (data.Length - dataIdx) / (bitDepth / 8);
        float[] samples = new float[sampleCount];

        if (bitDepth == 16)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short s = System.BitConverter.ToInt16(data, dataIdx + i * 2);
                samples[i] = s / 32768f;
            }
        }
        else if (bitDepth == 8)
        {
            for (int i = 0; i < sampleCount; i++)
                samples[i] = (data[dataIdx + i] - 128) / 128f;
        }

        AudioClip clip = AudioClip.Create("BalderCrush",
            sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public void Launch(EnvironmentController ec, Vector3 dir, float squishDuration)
    {
        this.ec = ec;
        this.direction = dir;
        this.squishDuration = squishDuration;

        entity = GetComponent<Entity>();
        if (entity == null)
        {
            MyPlugin.Log.LogError("ToyBazookaProjectile: entity is null!");
            Destroy(gameObject);
            return;
        }

        entity.ExternalActivity.moveMods.Clear();
        entity.Initialize(ec, transform.position);
        entity.SetActive(true);
        entity.OnEntityMoveInitialCollision += OnWallHit;
        flying = true;
    }

    private void Update()
    {
        if (!flying || ec == null || entity == null) return;
        entity.UpdateInternalMovement(direction * speed * ec.EnvironmentTimeScale);
        lifetime -= Time.deltaTime * ec.EnvironmentTimeScale;
        if (lifetime <= 0f) Destroy(gameObject);
    }

    private void OnWallHit(RaycastHit hit)
    {
        if (!flying) return;
        flying = false;
        Destroy(gameObject);
    }

    public void EntityTriggerEnter(
        Entity otherEntity, Collider other, bool validCollision)
    {
        if (!validCollision || !flying) return;
        if (other.CompareTag("Player")) return;
        if (otherEntity == null || otherEntity == entity) return;

        flying = false;
        otherEntity.ExternalActivity.moveMods.Clear();

        if (SquishSound != null)
        {
            PropagatedAudioManager pam =
                other.GetComponentInParent<PropagatedAudioManager>();
            if (pam != null)
                pam.PlaySingle(SquishSound);
            else
                Singleton<CoreGameManager>.Instance.audMan.PlaySingle(SquishSound);
        }

        if (!otherEntity.Squished)
            otherEntity.Squish(squishDuration);

        if (ec != null)
            ec.MakeNoise(otherEntity.transform.position, 127);

        Destroy(gameObject);
    }

    public void EntityTriggerStay(
        Entity otherEntity, Collider other, bool validCollision)
    { }

    public void EntityTriggerExit(
        Entity otherEntity, Collider other, bool validCollision)
    { }
}
