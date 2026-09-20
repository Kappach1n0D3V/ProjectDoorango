using System.Collections.Generic;
using Durango.Utils.Converter;
using Newtonsoft.Json;

namespace Durango.Utils;

// พอร์ตจาก L10N.Gettext (assembly แยกที่ไม่มีใน nexonSRC) — รูปร่าง JSON เดียวกับที่
// GettextConverter อ่าน: object {msgid: {locale: ข้อความ}|null} หรือ string ตรง ๆ
// ความต่างจากต้นฉบับ: ต้นฉบับ resolve ตาม locale ของ client เครื่อง host
// English server text: explicit en_US translation, English catalog, then original msgid.
[JsonConverter(typeof(GettextConverter))]
public class Gettext
{
    public string MsgId;
    public Dictionary<string, string> Dict;

    public Gettext() { }

    public Gettext(string msgId)
    {
        MsgId = msgId;
    }

    public Gettext(string msgId, Dictionary<string, string> dict)
    {
        MsgId = msgId;
        Dict = dict;
    }

    public override string ToString()
    {
        if (Dict != null && Dict.TryGetValue("en_US", out var english) && !string.IsNullOrEmpty(english))
            return english;
        if (!string.IsNullOrEmpty(MsgId) && Durango.Online.MoCatalog.Ready)
            return Durango.Online.MoCatalog.Translate(MsgId);
        return MsgId ?? string.Empty;
    }

    public static implicit operator string(Gettext g) => g?.ToString() ?? string.Empty;
}
