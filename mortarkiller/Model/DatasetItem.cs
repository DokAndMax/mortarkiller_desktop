using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace mortarkiller.Model;

public class DatasetItem
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("gt_players")] public List<Coordinate> GtPlayers { get; set; } = [];
    [JsonPropertyName("gt_pins")] public List<Coordinate> GtPins { get; set; } = [];
}

public class Coordinate
{
    [JsonPropertyName("x")]  public int X { get; set; }
    [JsonPropertyName("y")]  public int Y { get; set; }
}