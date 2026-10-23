using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

internal static class GradingCoordinates
{
    public static Region FromModel(JsonElement value)
    {
        GradingTools.Fields(value, "page_id", "bbox");
        var bounds = value.GetProperty("bbox");
        if (bounds.ValueKind != JsonValueKind.Array || bounds.GetArrayLength() != 4)
            throw new DomainValidationException("bbox must contain [left,top,right,bottom] as four integers from 0 to 999.");
        var coordinates = new int[4];
        for (int index = 0; index < 4; index++)
            if (!bounds[index].TryGetInt32(out coordinates[index]) || coordinates[index] is < 0 or > 999)
                throw new DomainValidationException("bbox coordinates must be integers from 0 to 999.");
        if (coordinates[0] >= coordinates[2] || coordinates[1] >= coordinates[3])
            throw new DomainValidationException("bbox requires left < right and top < bottom.");
        return new Region
        {
            PageId = value.GetProperty("page_id").GetString() ?? throw new JsonException(),
            X = coordinates[0] / 999d,
            Y = coordinates[1] / 999d,
            Width = (coordinates[2] - coordinates[0]) / 999d,
            Height = (coordinates[3] - coordinates[1]) / 999d
        };
    }

    public static JsonObject ToModelRegion(Region region)
    {
        int Coordinate(double value) => (int)Math.Clamp(Math.Round(value * 999), 0, 999);
        int left = Math.Min(998, Coordinate(region.X)), top = Math.Min(998, Coordinate(region.Y));
        return new()
        {
            ["page_id"] = region.PageId,
            ["bbox"] = new JsonArray(left, top, Math.Max(left + 1, Coordinate(region.X + region.Width)), Math.Max(top + 1, Coordinate(region.Y + region.Height)))
        };
    }

    public static void ToStoredRegions(JsonNode? node)
    {
        if (node is JsonArray array) { foreach (var item in array) ToStoredRegions(item); }
        if (node is not JsonObject obj) return;
        if (obj["region"] is JsonObject region)
        {
            using var json = JsonDocument.Parse(region.ToJsonString());
            obj["region"] = JsonNode.Parse(DomainJson.Serialize(FromModel(json.RootElement)));
        }
        foreach (var property in obj.Where(property => property.Key != "region")) ToStoredRegions(property.Value);
    }
}
