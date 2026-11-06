using Health360.Domain.Units;

namespace Health360.Domain.Training;

public sealed record AvailablePlate(double Weight, string Unit, int Count);
public sealed record LoadedPlate(double Weight, string Unit, int PerSide);
public sealed record PlateLoad(double TotalKg, IReadOnlyList<LoadedPlate> Plates);

/// <summary>Balanced pairs from a finite inventory, rounded down so a suggestion never adds unplanned load.</summary>
public static class PlateLoading
{
    public static PlateLoad Calculate(double targetKg, double barKg, IReadOnlyList<AvailablePlate> inventory)
    {
        if (!double.IsFinite(targetKg) || targetKg < 0 || !double.IsFinite(barKg) || barKg < 0
            || inventory.Count > 40 || inventory.Any(p => !double.IsFinite(p.Weight) || p.Weight <= 0 || p.Weight > 100
                || p.Unit is not ("kg" or "lb") || p.Count is < 1 or > 100))
        {
            throw new ArgumentException("Choose a finite load, bar and valid plate inventory.");
        }

        // One gram resolution bounds work to 1,000,001 states even with unusual mixed-unit inventories.
        // The final total uses exact canonical weights and is checked against the requested load.
        var plates = inventory.GroupBy(p => (p.Weight, p.Unit))
            .Select(g => new AvailablePlate(g.Key.Weight, g.Key.Unit, g.Sum(p => p.Count)))
            .OrderByDescending(p => UnitCodes.Convert(p.Weight, p.Unit, "kg")).ToArray();
        var capacity = Math.Min(1_000_000, (int)Math.Ceiling(Math.Max(0, Math.Min(1000, (targetKg - barKg) / 2) * 1000)));
        var states = new Dictionary<int, (double Kg, int[] Counts)> { [0] = (0, new int[plates.Length]) };
        for (var i = 0; i < plates.Length; i++)
        {
            var kg = UnitCodes.Convert(plates[i].Weight, plates[i].Unit, "kg")!.Value;
            var before = states.Values.ToArray();
            foreach (var (Kg, Counts) in before)
            {
                for (var count = 1; count <= plates[i].Count / 2; count++)
                {
                    var total = Kg + kg * count;
                    if (barKg + 2 * total > targetKg + 1e-9) break;
                    var key = (int)Math.Round(total * 1000);
                    if (key > capacity) break;
                    if (!states.TryGetValue(key, out var old) || total > old.Kg)
                    {
                        var counts = (int[])Counts.Clone();
                        counts[i] = count;
                        states[key] = (total, counts);
                    }
                }
            }
        }

        var best = states.Values.MaxBy(s => s.Kg);
        return new(barKg + 2 * best.Kg,
            [.. plates.Select((p, i) => new LoadedPlate(p.Weight, p.Unit, best.Counts[i])).Where(p => p.PerSide > 0)]);
    }
}
