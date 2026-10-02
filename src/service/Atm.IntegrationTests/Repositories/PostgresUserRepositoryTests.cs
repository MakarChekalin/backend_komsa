using Atm.Domain;
using Atm.Infrastructure;
using FluentAssertions;

namespace Atm.IntegrationTests.Repositories;

public class PostgresUserRepositoryTests : RepositoryTestBase
{
    public PostgresUserRepositoryTests(AtmDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task SaveAsync_ThenExistsAsync_ShouldReturnTrue()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        var user = new User("user-1", DateTime.UtcNow);

        await repository.SaveAsync(user);

        bool exists = await repository.ExistsAsync("user-1");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_WithUnknownId_ShouldReturnFalse()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);

        bool exists = await repository.ExistsAsync("ghost");

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_WhenAlreadyExists_ShouldBeIdempotent()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        var user = new User("user-1", DateTime.UtcNow);

        await repository.SaveAsync(user);
        await repository.SaveAsync(user);

        bool exists = await repository.ExistsAsync("user-1");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task GetExternalIdAsync_ForSavedUser_ShouldReturnGeneratedNumericId()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new User("user-1", DateTime.UtcNow));

        long? externalId = await repository.GetExternalIdAsync("user-1");

        externalId.Should().NotBeNull();
        externalId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetExternalIdAsync_ForDifferentUsers_ShouldReturnDifferentIds()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new User("user-1", DateTime.UtcNow));
        await repository.SaveAsync(new User("user-2", DateTime.UtcNow));

        long? first = await repository.GetExternalIdAsync("user-1");
        long? second = await repository.GetExternalIdAsync("user-2");

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task GetExternalIdAsync_WhenSavedTwice_ShouldKeepTheSameId()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        var user = new User("user-1", DateTime.UtcNow);

        await repository.SaveAsync(user);
        long? before = await repository.GetExternalIdAsync("user-1");
        await repository.SaveAsync(user);
        long? after = await repository.GetExternalIdAsync("user-1");

        after.Should().Be(before);
    }

    [Fact]
    public async Task GetExternalIdAsync_WithUnknownId_ShouldReturnNull()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);

        long? externalId = await repository.GetExternalIdAsync("ghost");

        externalId.Should().BeNull();
    }

    [Fact]
    public async Task ExistsByExternalIdAsync_WithExistingExternalId_ShouldReturnTrue()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new User("user-1", DateTime.UtcNow));
        long externalId = (await repository.GetExternalIdAsync("user-1")).GetValueOrDefault();

        bool exists = await repository.ExistsByExternalIdAsync(externalId);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsByExternalIdAsync_WithUnknownExternalId_ShouldReturnFalse()
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);

        bool exists = await repository.ExistsByExternalIdAsync(long.MaxValue);

        exists.Should().BeFalse();
    }
}
