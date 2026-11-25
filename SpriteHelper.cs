using BepInEx;
using System.IO;
using UnityEngine;

public static class SpriteHelper
{
    // WAVファイルからSoundObjectを生成
    public static SoundObject LoadSound(string fileName, string soundName = null, string subtitleText = null, Color subtitleColor = default)
    {
        string path = Path.Combine(Paths.PluginPath, "RitlonBBPMod", "textures", fileName);
        if (!File.Exists(path))
        {
            MyPlugin.Log.LogError($"Sound not found: {path}");
            return null;
        }
        AudioClip clip = LoadWav(path, soundName ?? fileName);
        if (clip == null)
        {
            MyPlugin.Log.LogError($"Failed to parse WAV: {fileName}");
            return null;
        }
        SoundObject so = ScriptableObject.CreateInstance<SoundObject>();
        so.name = soundName ?? fileName;
        so.soundClip = clip;

        // SoundObjectのpitch/minPitch/maxPitchはバージョンによりフィールド名が異なるためリフレクションで設定
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
        foreach (string fieldName in new[] { "pitch", "minPitch", "maxPitch" })
        {
            var f = typeof(SoundObject).GetField(fieldName, flags);
            try { f?.SetValue(so, 1f); } catch { }
        }

        // subtitleの設定
        if (subtitleText != null)
            SetSubtitle(so, subtitleText, subtitleColor);
        else
            so.subtitle = false;

        return so;
    }

    private static AudioClip LoadWav(string path, string name)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 44)
        {
            MyPlugin.Log.LogError($"WAV too short: {path}");
            return null;
        }

        // チャンクを順にたどって "fmt " と "data" を探す
        // （JUNK や LIST などの余分なチャンクが前後にあっても正しく読めるように）
        int channels = 0, sampleRate = 0, bitDepth = 0;
        int dataIdx = -1, dataSize = 0;
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            int size = System.BitConverter.ToInt32(data, pos + 4);
            int body = pos + 8;
            if (size < 0) break;

            if (id == "fmt " && body + 16 <= data.Length)
            {
                channels = System.BitConverter.ToInt16(data, body + 2);
                sampleRate = System.BitConverter.ToInt32(data, body + 4);
                bitDepth = System.BitConverter.ToInt16(data, body + 14);
            }
            else if (id == "data")
            {
                dataIdx = body;
                dataSize = System.Math.Min(size, data.Length - body);
            }
            pos = body + size + (size & 1);
        }

        MyPlugin.Log.LogInfo($"[LoadWav] {name}: channels={channels}, sampleRate={sampleRate}, bitDepth={bitDepth}");

        // ガード: 無効な値ならデフォルトを使う
        if (channels <= 0) channels = 1;
        if (sampleRate <= 0) sampleRate = 44100;
        if (bitDepth != 8 && bitDepth != 16) bitDepth = 16;

        if (dataIdx < 0)
        {
            MyPlugin.Log.LogError($"WAV 'data' chunk not found: {path}");
            return null;
        }

        int bytesPerSample = bitDepth / 8;
        int sampleCount = dataSize / bytesPerSample;
        sampleCount -= sampleCount % channels; // チャンネル数の倍数にそろえる
        if (sampleCount <= 0)
        {
            MyPlugin.Log.LogError($"WAV sampleCount=0: {path}");
            return null;
        }

        float[] samples = new float[sampleCount];
        if (bitDepth == 16)
            for (int i = 0; i < sampleCount; i++)
            {
                short s = System.BitConverter.ToInt16(data, dataIdx + i * 2);
                samples[i] = s / 32768f;
            }
        else // 8-bit
            for (int i = 0; i < sampleCount; i++)
                samples[i] = (data[dataIdx + i] - 128) / 128f;

        AudioClip clip = AudioClip.Create(name, sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    // SoundObjectにsubtitleを設定する
    // PlaySingle内で file.color が色として使われるため、SoundObject.color に直接設定する
    public static void SetSubtitle(SoundObject so, string text, Color color = default)
    {
        if (so == null) return;
        so.subtitle = true;

        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;

        // soundKey にテキストを直接セット（ローカライズキー未登録→キーがそのまま表示される）
        typeof(SoundObject).GetField("soundKey", flags)?.SetValue(so, text);

        // subDuration: クリップの長さに合わせる（なければデフォルト3秒）
        float duration = so.soundClip != null ? so.soundClip.length : 3f;
        typeof(SoundObject).GetField("subDuration", flags)?.SetValue(so, duration);

        // 色は SoundObject.color に直接セット（PlaySingle内で file.color が参照される）
        if (color != default(Color))
            typeof(SoundObject).GetField("color", flags)?.SetValue(so, color);
    }

    // 後方互換：呼び出し箇所が多いので空メソッドとして残す（削除しても良い）
    public static void ApplySubtitleColor(AudioManager audMan, SoundObject so) { }

    // ネームタグ装備中かどうかを判定 (PlayerManager.tagged フィールド)
    private static System.Reflection.FieldInfo _taggedField = null;

    public static bool HasNametag(PlayerManager pm)
    {
        if (pm == null) return false;
        if (_taggedField == null)
        {
            _taggedField = typeof(PlayerManager).GetField("tagged",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
        }
        if (_taggedField == null) return false;
        try { return (bool)_taggedField.GetValue(pm); }
        catch { return false; }
    }

    public static Sprite LoadSprite(string fileName)
    {
        string path = System.IO.Path.Combine(
            Paths.PluginPath, "RitlonBBPMod", "textures", fileName);
        if (!System.IO.File.Exists(path))
        {
            MyPlugin.Log.LogError($"Texture not found: {path}");
            return null;
        }
        byte[] data = System.IO.File.ReadAllBytes(path);
        Texture2D tex = new Texture2D(2, 2);
        tex.LoadImage(data);
        tex.filterMode = FilterMode.Point;
        return Sprite.Create(tex,
            new Rect(0, 0, tex.width, tex.height),
            new Vector2(0.5f, 0.5f), 100f);
    }

    public static Texture2D LoadTexture(string fileName)
    {
        string path = System.IO.Path.Combine(
            Paths.PluginPath, "RitlonBBPMod", "textures", fileName);
        if (!System.IO.File.Exists(path))
        {
            MyPlugin.Log.LogError($"Texture not found: {path}");
            return null;
        }
        byte[] data = System.IO.File.ReadAllBytes(path);
        Texture2D tex = new Texture2D(2, 2);
        tex.LoadImage(data);
        tex.filterMode = FilterMode.Point;
        return tex;
    }
}