using FsCheck.Fluent;
using Health360.Domain.Training;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Training;

public class PersonalRecordsTests
{
    private static LiftedSet Set(double kg, int reps) =>
        new(kg, reps, OneRepMax.Estimate(kg, reps, OneRepMaxFormula.Epley));

    private static RecordKind[] Kinds(IReadOnlyList<LiftedSet> earlier, LiftedSet set) =>
        [.. PersonalRecords.Detect(earlier, set).Select(r => r.Kind)];

    [Fact]
    public void The_first_set_of_an_exercise_is_a_baseline_not_a_record() =>
        Assert.Empty(PersonalRecords.Detect([], Set(100, 5)));

    [Fact]
    public void A_heavier_set_with_the_same_reps_beats_every_load_record()
    {
        var records = PersonalRecords.Detect([Set(100, 5)], Set(102.5, 5));

        Assert.Equal([RecordKind.E1rm, RecordKind.Weight, RecordKind.Volume], records.Select(r => r.Kind));
        Assert.Equal(102.5 * (1 + (5 / 30.0)), records[0].Value);
        Assert.Equal(102.5, records[1].Value);
        Assert.Equal(512.5, records[2].Value);
    }

    [Fact]
    public void More_reps_at_the_same_load_is_a_rep_record()
    {
        var records = PersonalRecords.Detect([Set(100, 5), Set(80, 10)], Set(100, 6));

        Assert.Equal([RecordKind.E1rm, RecordKind.Reps], records.Select(r => r.Kind));
        Assert.Equal(6, records[1].Value);
    }

    [Fact]
    public void A_rep_record_needs_an_earlier_set_at_that_load() =>
        Assert.DoesNotContain(RecordKind.Reps, Kinds([Set(100, 5)], Set(90, 12)));

    [Fact]
    public void A_higher_volume_at_a_lighter_load_is_a_volume_record_only() =>
        Assert.Equal([RecordKind.Volume], Kinds([Set(100, 5)], Set(60, 9)));

    [Fact]
    public void Matching_a_record_is_not_beating_it() =>
        Assert.Empty(PersonalRecords.Detect([Set(100, 5)], Set(100, 5)));

    [Fact]
    public void An_e1rm_record_needs_an_earlier_estimate_to_beat()
    {
        // 12 reps give no estimate (OneRepMax.MaxReps), so there's nothing to beat yet; the load records still apply.
        var records = PersonalRecords.Detect([Set(30, 12)], Set(80, 5));

        Assert.Equal([RecordKind.Weight, RecordKind.Volume], records.Select(r => r.Kind));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 5)]
    public void A_set_without_load_or_reps_sets_nothing(double kg, int reps) =>
        Assert.Empty(PersonalRecords.Detect([Set(50, 5)], new LiftedSet(kg, reps, null)));

    [Fact]
    public void Sets_that_do_not_count_are_not_a_baseline() =>
        Assert.Empty(PersonalRecords.Detect([new LiftedSet(0, 20, null)], Set(40, 10)));

    [Fact]
    public void Switching_units_never_creates_a_record()
    {
        // A set logged in lb, then the same plates entered in kg (or the reverse): the canonical kg values differ only
        // by conversion rounding, which is never a record.
        Holds(Gen.Zip(Double(1, 400), Int(1, OneRepMax.MaxReps)), t =>
        {
            var (lb, reps) = t;
            var fromLb = Quantity.Create(QuantityKind.Load, lb, Unit.Pound).Canonical;
            var roundTrip = Quantity.Create(QuantityKind.Load, Conversion.Convert(fromLb, Unit.Kilogram, Unit.Pound), Unit.Pound).Canonical;
            return PersonalRecords.Detect([Set(fromLb, reps)], Set(roundTrip, reps)).Count == 0
                && PersonalRecords.Detect([Set(roundTrip, reps)], Set(fromLb, reps)).Count == 0;
        });
    }

    [Fact]
    public void Every_record_beats_the_earlier_best_of_its_kind()
    {
        var sets = Gen.Zip(Double(1, 300), Int(1, 15)).Select(t => Set(t.Item1, t.Item2));
        Holds(Gen.Zip(sets.ListOf(), sets), t =>
        {
            var (earlier, set) = t;
            var counted = earlier.Where(s => s.Counts).ToList();
            return PersonalRecords.Detect(earlier, set).All(r => r.Kind switch
            {
                RecordKind.E1rm => counted.All(s => s.E1rmKg is null || r.Value > s.E1rmKg),
                RecordKind.Weight => counted.All(s => r.Value > s.WeightKg),
                RecordKind.Reps => counted.All(s => !Quantity.CanonicalEquals(s.WeightKg, set.WeightKg) || r.Value > s.Reps),
                RecordKind.Volume => counted.All(s => r.Value > s.VolumeKg),
                _ => false,
            });
        });
    }
}
