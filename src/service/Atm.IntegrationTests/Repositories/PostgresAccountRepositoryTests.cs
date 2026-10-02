using Atm.Domain;
using Atm.Infrastructure;
using FluentAssertions;

namespace Atm.IntegrationTests.Repositories;

public class PostgresAccountRepositoryTests : RepositoryTestBase
{
    public PostgresAccountRepositoryTests(AtmDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task SaveAsync_ThenGetByNumberAsync_ShouldReturnAccount()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        var account = new Account("40817123", "1234", "user-1", 500.75m);

        await repository.SaveAsync(account);

        Account? loaded = await repository.GetByNumberAsync("40817123");

        Assert.NotNull(loaded);
        loaded.Number.Should().Be("40817123");
        loaded.PinCode.Should().Be("1234");
        loaded.UserId.Should().Be("user-1");
        loaded.Balance.Should().Be(500.75m);
    }

    [Fact]
    public async Task GetByNumberAsync_WithUnknownNumber_ShouldReturnNull()
    {
        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);

        Account? loaded = await repository.GetByNumberAsync("does-not-exist");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_WhenAccountExists_ShouldUpdateBalanceOnly()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        var account = new Account("40817123", "1234", "user-1", 100);
        await repository.SaveAsync(account);

        account.Deposit(400);
        await repository.SaveAsync(account);

        Account? loaded = await repository.GetByNumberAsync("40817123");

        Assert.NotNull(loaded);
        loaded.Balance.Should().Be(500);
        loaded.PinCode.Should().Be("1234");
    }

    [Fact]
    public async Task ExistsAsync_WithExistingAccount_ShouldReturnTrue()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new Account("40817123", "1234", "user-1", 0));

        bool exists = await repository.ExistsAsync("40817123");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_WithUnknownAccount_ShouldReturnFalse()
    {
        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);

        bool exists = await repository.ExistsAsync("does-not-exist");

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task CountByUserIdAsync_ShouldCountOnlyAccountsOfThatUser()
    {
        await SeedUserAsync("user-1");
        await SeedUserAsync("user-2");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new Account("acc-1", "1111", "user-1", 0));
        await repository.SaveAsync(new Account("acc-2", "1111", "user-1", 0));
        await repository.SaveAsync(new Account("acc-3", "1111", "user-2", 0));

        int count = await repository.CountByUserIdAsync("user-1");

        count.Should().Be(2);
    }

    [Fact]
    public async Task CountByUserIdAsync_WithNoAccounts_ShouldReturnZero()
    {
        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);

        int count = await repository.CountByUserIdAsync("user-without-accounts");

        count.Should().Be(0);
    }

    [Theory]
    [InlineData(AccountType.Personal)]
    [InlineData(AccountType.Corporate)]
    public async Task SaveAsync_ThenGetByNumberAsync_ShouldRoundTripAccountType(AccountType type)
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new Account("40817123", "1234", "user-1", 0, type));

        Account? loaded = await repository.GetByNumberAsync("40817123");

        Assert.NotNull(loaded);
        loaded.Type.Should().Be(type);
    }

    [Fact]
    public async Task SaveAsync_ShouldReturnExternalIdMatchingTheLoadedAccount()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);

        long externalId = await repository.SaveAsync(new Account("40817123", "1234", "user-1", 0));
        Account? loaded = await repository.GetByNumberAsync("40817123");

        Assert.NotNull(loaded);
        externalId.Should().BeGreaterThan(0);
        loaded.ExternalId.Should().Be(externalId);
    }

    [Fact]
    public async Task SaveAsync_ForDifferentAccounts_ShouldReturnDifferentExternalIds()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);

        long first = await repository.SaveAsync(new Account("acc-1", "1111", "user-1", 0));
        long second = await repository.SaveAsync(new Account("acc-2", "1111", "user-1", 0));

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task SaveAsync_WhenAccountExists_ShouldReturnTheSameExternalId()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        var account = new Account("40817123", "1234", "user-1", 100);

        long first = await repository.SaveAsync(account);
        account.Deposit(50);
        long second = await repository.SaveAsync(account);

        second.Should().Be(first);
    }

    [Fact]
    public async Task GetByNumberForUpdateAsync_ShouldReturnTheSameAccountAsGetByNumberAsync()
    {
        await SeedUserAsync("user-1");

        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new Account("40817123", "1234", "user-1", 321.5m, AccountType.Corporate));

        Account? plain = await repository.GetByNumberAsync("40817123");
        Account? locked = await repository.GetByNumberForUpdateAsync("40817123");

        Assert.NotNull(plain);
        Assert.NotNull(locked);
        locked.Number.Should().Be(plain.Number);
        locked.UserId.Should().Be(plain.UserId);
        locked.Balance.Should().Be(321.5m);
        locked.Type.Should().Be(AccountType.Corporate);
        locked.ExternalId.Should().Be(plain.ExternalId);
    }

    [Fact]
    public async Task GetByNumberForUpdateAsync_WithUnknownNumber_ShouldReturnNull()
    {
        var repository = new PostgresAccountRepository(Fixture.ConnectionProvider);

        Account? loaded = await repository.GetByNumberForUpdateAsync("does-not-exist");

        loaded.Should().BeNull();
    }

    private async Task SeedUserAsync(string id)
    {
        var repository = new PostgresUserRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new User(id, DateTime.UtcNow));
    }
}
