using System.Text.Json;

namespace SpireReplay.SpireReplayCode;

public static class RunSummary
{
    public static string RoomName(string type) => type switch
    {
        "Monster" => "战斗", "Elite" => "精英", "Boss" => "首领", "Event" => "事件",
        "Shop" => "商店", "RestSite" => "休息处", "Treasure" => "宝箱", _ => type
    };
    public static List<string> Describe(RunRoomRecord room)
    {
        var lines = new List<string>();
        var potionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in room.Events.Concat(room.Battle?.Events ?? []))
        {
            var data = JsonSerializer.SerializeToElement(entry.Data);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("details", out var detail)) continue;
            string id = S(detail, "potionId"), name = S(detail, "name");
            if (id.Length > 0 && name.Length > 0) potionNames[id] = name;
        }
        foreach (var entry in CancelledSelectionFilter.Effective(room.Events))
        {
            var data = JsonSerializer.SerializeToElement(entry.Data);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("details", out var d) || d.ValueKind != JsonValueKind.Object) continue;
            switch (entry.Type)
            {
                case "event_options" when d.TryGetProperty("page", out var page) && page.GetInt32() == 0:
                    lines.Add(S(d, "name")); break;
                case "event_option_started": lines.Add("选择 " + S(d.GetProperty("option"), "title")); break;
                case "event_option_finished":
                    AddCards(d, "cardsAdded", "获得 ", lines);
                    AddCards(d, "cardsRemoved", "移除 ", lines);
                    break;
                case "shop_purchase_started":
                    string item = d.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? S(i, "name") : "删牌服务";
                    lines.Add("购买 " + item + " · " + S(d, "price") + " 金币"); break;
                case "shop_purchase_finished":
                    if (S(d, "success") != "True") lines.Add("购买结果：" + S(d, "failureReason"));
                    AddCards(d, "cardsRemoved", "移除 ", lines); break;
                case "rest_option_started": lines.Add(S(d, "name")); break;
                case "reward_card_chosen":
                    lines.Add(d.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object ? "获得 " + S(card, "name") : "跳过卡牌奖励"); break;
                case "relic_obtain_finished" when S(d, "completed") == "True":
                    lines.Add("获得遗物 " + S(d.GetProperty("relic"), "name")); break;
                case "relic_remove_finished" when S(d, "completed") == "True":
                    lines.Add("移除遗物 " + S(d.GetProperty("relic"), "name")); break;
                case "potion_use_started": lines.Add("使用 " + S(d, "name")); break;
                case "potion_discard_started": lines.Add("丢弃 " + S(d, "name")); break;
                case "potion_event_consumption_started": lines.Add("交出 " + S(d, "name")); break;
                case "reward_selection_finished" when S(d, "outcome") == "obtained":
                    var remaining = d.GetProperty("remaining");
                    if (S(remaining, "type") != "CardReward")
                        lines.Add("领取 " + (S(remaining, "gold") is { Length: > 0 } gold ? gold + " 金币" : potionNames.GetValueOrDefault(S(remaining, "potionId"), S(remaining, "relicId") + S(remaining, "potionId"))));
                    break;
                case "cards_selected":
                    if (d.TryGetProperty("cards", out var cards)) lines.Add("选牌 " + string.Join("、", cards.EnumerateArray().Select(c => S(c, "name"))));
                    break;
                case "card_upgrade_finished":
                    if (d.TryGetProperty("card", out var upgraded)) lines.Add("升级 " + S(upgraded, "name")); break;
            }
        }
        return lines;
    }
    private static string S(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) return property.Value.ToString();
        return "";
    }
    private static void AddCards(JsonElement details, string key, string verb, List<string> lines)
    {
        if (!details.TryGetProperty(key, out var cards) || cards.ValueKind != JsonValueKind.Array) return;
        foreach (var card in cards.EnumerateArray()) lines.Add(verb + S(card, "name"));
    }
}
