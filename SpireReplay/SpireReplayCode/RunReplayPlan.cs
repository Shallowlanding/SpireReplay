using System.Text.Json;
namespace SpireReplay.SpireReplayCode;

public static class RunReplayPlan
{
    public static bool IsInput(RecordedEvent entry) => entry.Type is "event_option_started" or "shop_purchase_started" or
        "rest_option_started" or "reward_selection_started" or "reward_card_chosen" or "cards_selected" or
        "potion_discard_started" or "potion_use_started" or "reward_rerolled";
    public static JsonElement Details(RecordedEvent entry)
    {
        var data = JsonSerializer.SerializeToElement(entry.Data);
        return Get(data, "details");
    }
    public static JsonElement Get(JsonElement data, string key)
    {
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var p in data.EnumerateObject())
                if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }
    public static string Text(JsonElement data, string key) => Get(data, key).ToString();
    public static int Number(JsonElement data, string key, int fallback = -1) => Get(data, key).TryNumber(fallback);
    private static int TryNumber(this JsonElement value, int fallback) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : fallback;
    public static string CardKey(JsonElement card) => Text(card, "modelId") + ":" + Number(card, "upgradeLevel", 0) + ":" + Number(card, "deckIndex");
    public static string RewardKey(JsonElement reward)
    {
        string key = Text(reward, "type") + "|" + Text(reward, "potionId") + "|" + Text(reward, "relicId") + "|" + Text(reward, "gold");
        if (Text(reward, "type") == "CardReward")
        {
            var cards = Get(reward, "cards");
            key += "|" + (cards.ValueKind == JsonValueKind.Array ? string.Join("|", cards.EnumerateArray()
                .Select(c => Text(c, "modelId") + ":" + Number(c, "upgradeLevel", 0))) : "missing-cards");
        }
        return key;
    }
    public static string Signature(RecordedEvent entry)
    {
        var d = Details(entry);
        string detail = entry.Type switch
        {
            "event_option_started" => Text(d, "eventId") + "|" + Text(Get(d, "option"), "key") + "|" + Number(Get(d, "option"), "index"),
            "shop_purchase_started" => Text(d, "entryType") + "|" + Text(Get(d, "item"), "modelId") + "|" + Number(d, "price"),
            "rest_option_started" => Text(d, "optionId"),
            "reward_selection_started" or "reward_rerolled" => RewardKey(d),
            "reward_card_chosen" => CardKey(Get(d, "card")) + "|" + Text(d, "alternative") + "|" + Text(d, "skipped"),
            "cards_selected" => Get(d, "cards").ValueKind == JsonValueKind.Array ? string.Join("|", Get(d, "cards").EnumerateArray().Select(CardKey)) : "invalid",
            _ => Text(d, "potionId") + "|" + Number(d, "slotIndex")
        };
        return entry.Type + ":" + detail;
    }
    public static bool Failed(RecordedEvent entry)
    {
        var d = Details(entry);
        return entry.Type switch
        {
            "event_option_finished" => Text(d, "outcome") is "cancelled" or "faulted",
            "shop_purchase_finished" or "rest_option_finished" => Text(d, "success") == "False",
            _ => false
        };
    }
    public static int MatchingPrefix(IReadOnlyList<RecordedEvent> source, IEnumerable<RecordedEvent> actual)
    {
        int count = 0;
        var entries = actual.ToList();
        if (entries.Any(Failed)) return -1;
        foreach (var entry in entries.Where(IsInput))
        {
            if (count >= source.Count || Signature(source[count]) != Signature(entry)) return -1;
            count++;
        }
        return count;
    }
    public static bool Compatible(RunRecording source, RunRecording current) => Mismatch(source, current) == null;
    public static string? Mismatch(RunRecording source, RunRecording current)
    {
        if (source.RunId == current.RunId) return "请选择历史版本，不能重打当前记录";
        if (!string.Equals(source.Seed, current.Seed, StringComparison.OrdinalIgnoreCase)) return $"种子不同：历史 {source.Seed}，当前 {current.Seed}";
        if (source.CharacterId != current.CharacterId) return $"角色不同：历史 {source.CharacterId}，当前 {current.CharacterId}";
        if (source.Ascension != current.Ascension) return $"进阶不同：历史 {source.Ascension}，当前 {current.Ascension}";
        if (source.GameAssemblyId != current.GameAssemblyId || string.IsNullOrEmpty(current.GameAssemblyId)) return "游戏程序集版本不同，需使用录制时的游戏版本";
        if (!source.Areas.Select(a => a.Id).SequenceEqual(current.Areas.Select(a => a.Id))) return "大层地图不同：历史 " + string.Join("/", source.Areas.Select(a => a.Name)) + "，当前 " + string.Join("/", current.Areas.Select(a => a.Name));
        bool seedModePair = source.GameMode is "Standard" or "Custom" && current.GameMode is "Standard" or "Custom";
        if (source.GameMode != current.GameMode && !seedModePair) return $"模式不兼容：历史 {source.GameMode}，当前 {current.GameMode}";
        // Standard archives predating modifier metadata have no custom rules.
        if (source.GameMode != current.GameMode && source.GameMode == "Custom" && source.ModifierIds == null)
            return "旧自定义记录未保存额外规则，无法确认与普通局兼容；请用自定义种子局重试";
        var oldModifiers = source.ModifierIds ?? [];
        var newModifiers = current.ModifierIds ?? [];
        if (!oldModifiers.Order(StringComparer.Ordinal).SequenceEqual(newModifiers.Order(StringComparer.Ordinal)))
            return "额外规则不同：历史 " + (oldModifiers.Count == 0 ? "无" : string.Join("/", oldModifiers)) + "，当前 " + (newModifiers.Count == 0 ? "无" : string.Join("/", newModifiers));
        return null;
    }
}
