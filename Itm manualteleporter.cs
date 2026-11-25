using System.Reflection;
using MTM101BaldAPI;
using MTM101BaldAPI.Registers;
using UnityEngine;

public class ITM_ManualTeleporter : Item
{
#pragma warning disable CS0649
    [SerializeField]
    private SoundObject audTeleport;
#pragma warning restore CS0649

    public override bool Use(PlayerManager pm)
    {
        // 最も近いエレベーターを探す
        Elevator nearest = null;
        float nearestDist = float.MaxValue;

        foreach (Elevator elev in pm.ec.Elevators)
        {
            float dist = Vector3.Distance(pm.transform.position,
                pm.ec.CellFromPosition(elev.Door.position).FloorWorldPosition);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = elev;
            }
        }

        if (nearest != null)
        {
            // ドアのセルを取得し、ドアの向き（廊下側）の1セル外にテレポート
            IntVector2 doorPos = nearest.Door.position;
            Cell doorCell = pm.ec.CellFromPosition(doorPos);

            // ドアの向きの先（廊下側）を探す
            IntVector2 destPos = doorPos;
            foreach (Direction dir in doorCell.doorDirs)
            {
                IntVector2 candidate = doorPos + dir.ToIntVector2();
                Cell candidateCell = pm.ec.CellFromPosition(candidate);
                // ドアの先がエレベーター室ではないセル＝廊下側
                if (candidateCell != null && !candidateCell.Null && candidateCell.room != doorCell.room)
                {
                    destPos = candidate;
                    break;
                }
            }

            Vector3 dest = pm.ec.CellFromPosition(destPos).FloorWorldPosition;
            pm.Teleport(dest);
            if (audTeleport != null)
                Singleton<CoreGameManager>.Instance.audMan.PlaySingle(audTeleport);
            MyPlugin.Log.LogInfo("[ManualTeleporter] Teleported outside elevator at: " + dest);
        }
        else
        {
            MyPlugin.Log.LogWarning("[ManualTeleporter] No elevator found!");
        }

        return true; // 使い切り
    }

    public static void Register()
    {
        Sprite spriteSmall = SpriteHelper.LoadSprite("teleporter_small.png");
        Sprite spriteLarge = SpriteHelper.LoadSprite("teleporter_large.png");

        // 既存のITM_TeleporterからaudTeleportを取得
        SoundObject teleportSound = null;
        foreach (ItemObject io in Resources.FindObjectsOfTypeAll<ItemObject>())
        {
            if (io.itemType == Items.Teleporter && io.item != null)
            {
                ITM_Teleporter orig = io.item as ITM_Teleporter;
                if (orig != null)
                {
                    teleportSound = (SoundObject)typeof(ITM_Teleporter)
                        .GetField("audTeleport", BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(orig);
                    break;
                }
            }
        }

        GameObject prefabGo = new GameObject("ManualTeleporter_Item");
        Object.DontDestroyOnLoad(prefabGo);
        ITM_ManualTeleporter itemComp = prefabGo.AddComponent<ITM_ManualTeleporter>();
        if (teleportSound != null)
        {
            typeof(ITM_ManualTeleporter)
                .GetField("audTeleport", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(itemComp, teleportSound);
        }

        ItemObject item = ScriptableObject.CreateInstance<ItemObject>();
        item.name = "ManualTeleporter";
        item.itemType = EnumExtensions.ExtendEnum<Items>("ManualTeleporter");
        item.item = itemComp;
        item.itemSpriteSmall = spriteSmall;
        item.itemSpriteLarge = spriteLarge;
        item.price = 450;
        item.addToInventory = true;
        item.nameKey = "ManualTeleporter";
        item.descKey = "ManualTeleporter_desc";

        ItemMetaData meta = new ItemMetaData(MyPlugin.Instance.Info, item);
        meta.flags = ItemFlags.None;
        ItemMetaStorage.Instance.Add(meta);

        // ローカライズ登録は LocalizationPatch.cs（LoadLocalizedText の Postfix）側で
        // 一括して行っている。LoadLocalizedText は辞書を丸ごと作り直すため、
        // ここで直接 Add しても意味がない（Start() 実行時に消される）。

        MyPlugin.Log.LogInfo("ManualTeleporter registered.");
    }
}
