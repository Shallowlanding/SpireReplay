using System.Text.Json;
using static SpireReplay.SpireReplayCode.RunReplayPlan;

namespace SpireReplay.SpireReplayCode;

public static class CancelledSelectionFilter
{
    public static List<RecordedEvent> Effective(IEnumerable<RecordedEvent> source)
    {
        var entries = source.ToList();
        var result = new List<RecordedEvent>();
        for (int i = 0; i < entries.Count; i++)
        {
            var start = entries[i]; var details = Details(start);
            bool rest = start.Type == "rest_option_started" && Text(details, "optionId") is "SMITH" or "COOK";
            bool shop = start.Type == "shop_purchase_started" && Text(details, "entryType") == "MerchantCardRemovalEntry";
            if (rest || shop)
            {
                int end = i + 1;
                while (end < entries.Count && EmptySelection(entries[end])) end++;
                if (end > i + 1 && end < entries.Count && entries[end].Type == (rest ? "rest_option_finished" : "shop_purchase_finished"))
                {
                    var finish = Details(entries[end]);
                    if (Text(finish, "completed") == "True" && Text(finish, "success") == "False" &&
                        Number(finish, "hpDelta", 0) == 0 && Number(finish, "goldDelta", 0) == 0 &&
                        (!shop || Get(finish, "cardsRemoved").ValueKind == JsonValueKind.Array && Get(finish, "cardsRemoved").GetArrayLength() == 0))
                    { i = end; continue; }
                }
            }
            result.Add(start);
        }
        return result;
    }
    private static bool EmptySelection(RecordedEvent entry)
    {
        if (entry.Type is not ("cards_selected" or "player_choice")) return false;
        var d = Details(entry); var cards = Get(d, "cards"); var indexes = Get(d, "indexes");
        return cards.ValueKind == JsonValueKind.Array && cards.GetArrayLength() == 0 &&
            (indexes.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || indexes.ValueKind == JsonValueKind.Array && indexes.GetArrayLength() == 0);
    }
    public static void Clean(RunRecording run)
    {
        foreach (var room in run.Rooms)
        {
            var effective = Effective(room.Events); room.Events.Clear(); room.Events.AddRange(effective);
        }
    }
}
