using System.Text;

namespace Poe2Map;

/// <summary>
///     Alanin varlik listesini okur ve canavarlari cikarir. Sadece calisma zamani kodu;
///     canli oyunda sinayan tanilama komutu MonstersCommand.cs'de.
///
///     Yapi (GameOffsets'ten, 11.09.2026 patch'inden onceki surum - bu yuzden her
///     halka kendi kendini siniyor):
///
///         alan + 0x6F0   std::map  uyanik varliklar    { bas dugum, boyut }
///         alan + 0x700   std::map  uyuyan varliklar
///         dugum          +0x00 sol  +0x08 ebeveyn  +0x10 sag  +0x18 renk  +0x19 bos mu
///                        +0x20 varlik no          +0x28 varlik isaretcisi
///         varlik         +0x08 ayrinti  +0x10 bilesen vektoru  +0x8C gecerlilik (bit0 = gecersiz)
///         ayrinti        +0x08 yol (std::wstring)  +0x28 bilesen isim tablosu
///         isim tablosu   +0x28 vektor: { isim isaretcisi, sira no } x N
///
///     16.09.2026'da canli oyunda dogrulandi: agactaki dugum sayisi basliktaki boyuta
///     birebir esit (84/84, 925/925), yollar ve bilesen isimleri dogru okunuyor.
/// </summary>
internal static class EntityReader
{
    internal const ulong AwakeMap = 0x6F0;
    internal const ulong SleepingMap = 0x700;

    // Bilesen icindeki alanlar. Patch bunlari kaydirmis olabilir; MonstersCommand siniyor.
    internal static ulong RenderPosition = 0x138;
    internal static ulong RarityOffset = 0x144;
    internal static ulong LifeTotal = 0x1DC;
    internal static ulong LifeCurrent = 0x1E0;
    internal static ulong Reaction = 0x1E0;

    internal readonly record struct MapHeader(ulong Head, int Size);

    internal sealed class EntityInfo
    {
        internal string Path = "";
        internal readonly Dictionary<string, ulong> Components = new(StringComparer.Ordinal);

        internal bool IsMonster => this.Path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal);
    }

    internal readonly record struct Monster(float X, float Y, int Rarity);

    internal static MapHeader? ReadMapHeader(IntPtr h, ulong address)
    {
        var buf = new byte[16];
        if (!Read(h, address, buf)) { return null; }

        var head = BitConverter.ToUInt64(buf, 0);
        var size = BitConverter.ToInt32(buf, 8);
        if (!PlayerChain.LooksLikePointer(head) || size < 0 || size > 50000) { return null; }
        return new MapHeader(head, size);
    }

    /// <summary>
    ///     Kirmizi-siyah agaci gezer. Oyun agaci ayni anda degistirdigi icin yarim
    ///     okunmus dugumler olabilir: rengi 0/1 olmayan ya da isaretcisi bozuk dal
    ///     sessizce atlaniyor, butun okuma iptal edilmiyor.
    /// </summary>
    internal static List<(uint Id, ulong Entity)> WalkMap(IntPtr h, MapHeader map, int max = 20000)
    {
        var result = new List<(uint, ulong)>();
        if (map.Size == 0) { return result; }

        var node = new byte[0x30];
        if (!Read(h, map.Head, node) || node[0x19] != 1) { return result; }

        var queue = new Queue<ulong>();
        var seen = new HashSet<ulong> { map.Head };
        var root = BitConverter.ToUInt64(node, 0x08);
        if (PlayerChain.LooksLikePointer(root)) { queue.Enqueue(root); }

        while (queue.Count > 0 && result.Count < max && seen.Count < (max * 2) + 2)
        {
            var at = queue.Dequeue();
            if (!seen.Add(at)) { continue; }
            if (!Read(h, at, node)) { continue; }
            if (node[0x19] != 0 || node[0x18] > 1) { continue; }

            var entity = BitConverter.ToUInt64(node, 0x28);
            if (PlayerChain.LooksLikePointer(entity)) { result.Add((BitConverter.ToUInt32(node, 0x20), entity)); }

            var left = BitConverter.ToUInt64(node, 0x00);
            var right = BitConverter.ToUInt64(node, 0x10);
            if (PlayerChain.LooksLikePointer(left)) { queue.Enqueue(left); }
            if (PlayerChain.LooksLikePointer(right)) { queue.Enqueue(right); }
        }

        return result;
    }

    /// <summary>Varligin yolunu ve bilesen adreslerini okur. Varlik basina bir kez yeterli.</summary>
    internal static EntityInfo? ReadInfo(IntPtr h, ulong entity)
    {
        var e = new byte[0x90];
        if (!Read(h, entity, e)) { return null; }
        if ((e[0x8C] & 1) != 0) { return null; }

        var details = BitConverter.ToUInt64(e, 0x08);
        var compFirst = BitConverter.ToUInt64(e, 0x10);
        var compLast = BitConverter.ToUInt64(e, 0x18);
        if (!PlayerChain.LooksLikePointer(details) || compLast < compFirst) { return null; }

        var compCount = (long)(compLast - compFirst) / 8;
        if (compCount <= 0 || compCount > 128) { return null; }

        var d = new byte[0x30];
        if (!Read(h, details, d)) { return null; }

        var path = ReadWString(h, d, 0x08);
        if (!path.StartsWith("Metadata/", StringComparison.Ordinal)) { return null; }

        var info = new EntityInfo { Path = path };

        var lookup = BitConverter.ToUInt64(d, 0x28);
        if (!PlayerChain.LooksLikePointer(lookup)) { return info; }

        var comps = new byte[compCount * 8];
        var lk = new byte[0x40];
        if (!Read(h, compFirst, comps) || !Read(h, lookup, lk)) { return info; }

        var bucketFirst = BitConverter.ToUInt64(lk, 0x28);
        var bucketLast = BitConverter.ToUInt64(lk, 0x30);
        if (bucketLast < bucketFirst) { return info; }

        var slots = (long)(bucketLast - bucketFirst) / 16;
        if (slots <= 0 || slots > 512) { return info; }

        var bucket = new byte[slots * 16];
        if (!Read(h, bucketFirst, bucket)) { return info; }

        for (var i = 0; i < slots; i++)
        {
            var namePtr = BitConverter.ToUInt64(bucket, (int)(i * 16));
            var index = BitConverter.ToInt32(bucket, (int)(i * 16) + 8);
            if (index < 0 || index >= compCount) { continue; }

            // Isimler cogu zaman modulun sabit metinleri; hizali olmak zorunda degiller.
            if (namePtr < 0x10000 || namePtr > 0x7FFFFFFFFFFF) { continue; }

            var name = ReadAscii(h, namePtr, 64);
            if (name.Length == 0) { continue; }

            info.Components[name] = BitConverter.ToUInt64(comps, index * 8);
        }

        return info;
    }

    /// <summary>
    ///     Bir bilesenin gercekten bu varliga ait oldugunu siniyor: bilesenin +0x08'i
    ///     sahibinin adresini tutuyor. Isim-sira eslemesi bozuksa burada yakalaniyor.
    /// </summary>
    internal static bool OwnedBy(IntPtr h, ulong component, ulong entity) =>
        PlayerChain.LooksLikePointer(component) && PlayerChain.ReadPointer(h, component + 0x08) == entity;

    /// <summary>
    ///     Canliyi, dusmani ve konumu okunabileni canavar olarak dondurur. Olu, dost
    ///     (kendi minyonlarin) ya da konumu bozuk olanlar false.
    /// </summary>
    internal static bool TryReadMonster(IntPtr h, ulong entity, EntityInfo info, out Monster monster)
    {
        monster = default;
        if (!info.IsMonster) { return false; }

        // "Monster" bileseni olmayanlar canavar degil: arena susleri, aura nesneleri.
        if (!info.Components.ContainsKey("Monster")) { return false; }

        // Sahiplik sart: 16.09.2026 olcumunde ayni konum ve canla iki kayit gorundu -
        // biri butun bilesenlerinin sahibiydi, digeri degildi. Ikincisi bayat bir kopya;
        // bu sinav olmadan her canavar haritada iki kez cizilirdi.
        if (!info.Components.TryGetValue("Render", out var render) || !OwnedBy(h, render, entity)) { return false; }

        var pos = new byte[12];
        if (!Read(h, render + RenderPosition, pos)) { return false; }
        var x = BitConverter.ToSingle(pos, 0);
        var y = BitConverter.ToSingle(pos, 4);
        if (!float.IsFinite(x) || !float.IsFinite(y) || x <= 0 || y <= 0 || x > 500000 || y > 500000) { return false; }

        if (info.Components.TryGetValue("Life", out var life))
        {
            var hp = new byte[8];
            if (Read(h, life + LifeTotal, hp))
            {
                var total = BitConverter.ToInt32(hp, 0);
                var current = BitConverter.ToInt32(hp, 4);
                if (total > 0 && current <= 0) { return false; }
            }
        }

        if (info.Components.TryGetValue("Positioned", out var positioned))
        {
            var r = new byte[1];
            if (Read(h, positioned + Reaction, r) && (r[0] & 0x7F) == 0x01) { return false; }
        }

        var rarity = 0;
        if (info.Components.TryGetValue("ObjectMagicProperties", out var magic))
        {
            var rb = new byte[4];
            if (Read(h, magic + RarityOffset, rb)) { rarity = Math.Clamp(BitConverter.ToInt32(rb, 0), 0, 3); }
        }

        monster = new Monster(x, y, rarity);
        return true;
    }

    // ------------------------------------------------------------------ yardimcilar

    internal static bool Read(IntPtr h, ulong address, byte[] buffer) =>
        address >= 0x10000 &&
        Native.ReadProcessMemory(h, (IntPtr)address, buffer, (IntPtr)buffer.Length, out var got) &&
        (long)got == buffer.Length;

    /// <summary>
    ///     std::wstring: { tampon/satir ici 16 bayt, ... , uzunluk +0x10, kapasite +0x18 }.
    ///     Kapasite 8'den kucukse metin isaretci degil, dogrudan ilk 16 baytin icinde.
    /// </summary>
    internal static string ReadWString(IntPtr h, byte[] owner, int at)
    {
        var length = BitConverter.ToInt32(owner, at + 0x10);
        var capacity = BitConverter.ToInt32(owner, at + 0x18);
        if (length <= 0 || length > 512 || capacity < length || capacity > 4096) { return ""; }

        if (capacity < 8)
        {
            return Encoding.Unicode.GetString(owner, at, Math.Min(length, 8) * 2);
        }

        var buffer = BitConverter.ToUInt64(owner, at);
        var bytes = new byte[length * 2];
        return Read(h, buffer, bytes) ? Encoding.Unicode.GetString(bytes) : "";
    }

    internal static string ReadAscii(IntPtr h, ulong address, int max)
    {
        var bytes = new byte[max];
        if (!Read(h, address, bytes)) { return ""; }

        var end = Array.IndexOf(bytes, (byte)0);
        if (end <= 0) { return ""; }

        for (var i = 0; i < end; i++)
        {
            if (bytes[i] < 0x20 || bytes[i] > 0x7E) { return ""; }
        }

        return Encoding.ASCII.GetString(bytes, 0, end);
    }
}
