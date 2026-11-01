using PowerSync.Common.DB.Schema;

namespace Health360.LocalDb.Tests;

public class SchemaTests
{
    private static readonly Dictionary<string, Table> Tables = AppSchema.Tables.ToDictionary(t => t.Name);

    [Fact]
    public void IncludesEveryUploadableTable()
    {
        foreach (var name in AppSchema.UploadableTables)
        {
            Assert.True(Tables.ContainsKey(name), name);
        }
    }

    [Fact]
    public void KeepsFailedUploadsLocalOnly()
    {
        Assert.True(Tables[AppSchema.FailedUploads].LocalOnly);
        Assert.True(Tables[PhotoUploader.Table].LocalOnly);
        Assert.True(Tables[PhotoUploader.DeletionsTable].LocalOnly);
        Assert.All(
            Tables.Values.Where(t => t.Name is not AppSchema.FailedUploads and not PhotoUploader.Table and not PhotoUploader.DeletionsTable),
            t => Assert.False(t.LocalOnly, t.Name));
    }

    [Fact]
    public void LeavesTheIdColumnToPowerSync()
    {
        Assert.All(Tables.Values, t => Assert.DoesNotContain("id", t.Columns.Keys));
    }

    [Fact]
    public void StoresJsonAsTextAndBooleansAsIntegers()
    {
        foreach (var (table, columns) in AppSchema.ServerColumnTypes)
        {
            foreach (var (column, type) in columns)
            {
                Assert.Equal(type == ServerColumnType.Json ? ColumnType.Text : ColumnType.Integer, Tables[table].Columns[column]);
            }
        }
    }

    [Fact]
    public void InsertOnlyTablesAreUploadable()
    {
        Assert.Subset(new HashSet<string>(AppSchema.UploadableTables), new HashSet<string>(AppSchema.InsertOnlyTables));
    }
}
