using Xunit;

namespace NutanixMcp.Data.Tests.Fixtures;

[CollectionDefinition("Pgvector")]
public class FixtureCollection : ICollectionFixture<PgvectorFixture>
{
}
