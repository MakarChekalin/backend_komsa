using Atm.Domain;
using Atm.Grpc;
using Atm.Infrastructure;
using FluentAssertions;
using Grpc.Core;

namespace Atm.IntegrationTests.Endpoints;

public class AccountEndpointTests : GrpcServiceTestBase
{
    public AccountEndpointTests(AtmGrpcServiceFixture grpc)
        : base(grpc)
    {
    }

    [Fact]
    public async Task CreateAccount_WithAdminSessionAndKnownOwner_ShouldSucceed()
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        Guid adminSession = await SeedAdminSessionAsync();
        await SeedUserAsync(owner);

        CreateAccountResponse response = await Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = adminSession.ToString(),
            Number = accountNumber,
            PinCode = "1234",
            OwnerUserId = owner,
        });

        response.Should().NotBeNull();

        Account? created = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(created);
        created.UserId.Should().Be(owner);
    }

    [Fact]
    public async Task CreateAccount_WithoutAdminSession_ShouldFailWithPermissionDenied()
    {
        string owner = UniqueId("owner");
        await SeedUserAsync(owner);

        Func<Task> act = () => Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = Guid.NewGuid().ToString(),
            Number = UniqueId("account"),
            PinCode = "1234",
            OwnerUserId = owner,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task CreateAccount_ForSixthAccountOfSameOwner_ShouldFailWithFailedPrecondition()
    {
        string owner = UniqueId("owner");
        string accountPrefix = UniqueId("account");
        Guid adminSession = await SeedAdminSessionAsync();
        await SeedUserAsync(owner);

        var accountRepository = new PostgresAccountRepository(Grpc.ConnectionProvider);

        for (int i = 0; i < 5; i++)
            await accountRepository.SaveAsync(new Account($"{accountPrefix}-{i}", "1234", owner, 0));

        Func<Task> act = () => Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = adminSession.ToString(),
            Number = $"{accountPrefix}-overflow",
            PinCode = "1234",
            OwnerUserId = owner,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Deposit_ToOwnAccount_ShouldIncreaseBalance()
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        await SeedUserAsync(owner);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(accountNumber, "1234", owner, 100));
        Guid session = await SeedUserSessionAsync(accountNumber);

        await Grpc.AccountClient.DepositAsync(new AmountRequest
        {
            SessionKey = session.ToString(),
            Amount = "50",
            AccountNumber = accountNumber,
            UserId = owner,
        });

        Account? account = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(account);
        account.Balance.Should().Be(150);
    }

    [Fact]
    public async Task Withdraw_WithSufficientFunds_ShouldDecreaseBalance()
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        await SeedUserAsync(owner);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(accountNumber, "1234", owner, 100));
        Guid session = await SeedUserSessionAsync(accountNumber);

        await Grpc.AccountClient.WithdrawAsync(new AmountRequest
        {
            SessionKey = session.ToString(),
            Amount = "40",
            AccountNumber = accountNumber,
            UserId = owner,
        });

        Account? account = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(account);
        account.Balance.Should().Be(60);
    }

    [Fact]
    public async Task Withdraw_WithInsufficientFunds_ShouldFailWithFailedPrecondition()
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        await SeedUserAsync(owner);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(accountNumber, "1234", owner, 10));
        Guid session = await SeedUserSessionAsync(accountNumber);

        Func<Task> act = () => Grpc.AccountClient.WithdrawAsync(new AmountRequest
        {
            SessionKey = session.ToString(),
            Amount = "40",
            AccountNumber = accountNumber,
            UserId = owner,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.FailedPrecondition);

        Account? account = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(account);
        account.Balance.Should().Be(10);
    }

    [Fact]
    public async Task Withdraw_FromAccountNotOwnedByCaller_ShouldFailWithPermissionDenied()
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        await SeedUserAsync(owner);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(accountNumber, "1234", owner, 100));
        Guid session = await SeedUserSessionAsync(accountNumber);

        Func<Task> act = () => Grpc.AccountClient.WithdrawAsync(new AmountRequest
        {
            SessionKey = session.ToString(),
            Amount = "10",
            AccountNumber = accountNumber,
            UserId = "someone-else",
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    public async Task Deposit_WithZeroOrNegativeAmount_ShouldFailWithInvalidArgumentAndKeepBalance(string amount)
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        await SeedUserAsync(owner);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(accountNumber, "1234", owner, 100));
        Guid session = await SeedUserSessionAsync(accountNumber);

        Func<Task> act = () => Grpc.AccountClient.DepositAsync(new AmountRequest
        {
            SessionKey = session.ToString(),
            Amount = amount,
            AccountNumber = accountNumber,
            UserId = owner,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);

        Account? account = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(account);
        account.Balance.Should().Be(100);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    public async Task Withdraw_WithZeroOrNegativeAmount_ShouldFailWithInvalidArgumentAndKeepBalance(string amount)
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        await SeedUserAsync(owner);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(accountNumber, "1234", owner, 100));
        Guid session = await SeedUserSessionAsync(accountNumber);

        Func<Task> act = () => Grpc.AccountClient.WithdrawAsync(new AmountRequest
        {
            SessionKey = session.ToString(),
            Amount = amount,
            AccountNumber = accountNumber,
            UserId = owner,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);

        Account? account = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(account);
        account.Balance.Should().Be(100);
    }

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<Guid> SeedAdminSessionAsync()
    {
        var session = new Session(SessionType.Admin);
        await new PostgresSessionRepository(Grpc.ConnectionProvider).SaveAsync(session);
        return session.Key;
    }

    private async Task<Guid> SeedUserSessionAsync(string accountNumber)
    {
        var session = new Session(SessionType.User, accountNumber);
        await new PostgresSessionRepository(Grpc.ConnectionProvider).SaveAsync(session);
        return session.Key;
    }

    private async Task SeedUserAsync(string id)
    {
        await new PostgresUserRepository(Grpc.ConnectionProvider).SaveAsync(new User(id, DateTime.UtcNow));
    }
}
