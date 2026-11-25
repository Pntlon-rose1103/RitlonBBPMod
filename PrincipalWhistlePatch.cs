using HarmonyLib;
using UnityEngine;

[HarmonyPatch(typeof(ITM_PrincipalWhistle), "Use")]
public class PrincipalWhistlePatch
{
    static void Postfix(PlayerManager pm)
    {
        foreach (NPC npc in pm.ec.Npcs)
        {
            MrWhistle whistle = npc.GetComponent<MrWhistle>();
            if (whistle == null) continue;
            float dist = Vector3.Distance(
                pm.transform.position, npc.transform.position);
            if (dist < 25f) whistle.Repel();
        }
    }
}
