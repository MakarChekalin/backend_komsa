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
    public async Task CreateAccount_WithCorporateType_ShouldPersistTypeAndAssignExternalId()
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        Guid adminSession = await SeedAdminSessionAsync();
        await SeedUserAsync(owner);

        await Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = adminSession.ToString(),
            Number = accountNumber,
            PinCode = "1234",
            OwnerUserId = owner,
            AccountType = Atm.Grpc.AccountType.Corporate,
        });

        Account? created = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(created);
        created.Type.Should().Be(Atm.Domain.AccountType.Corporate);
        created.ExternalId.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(Atm.Grpc.AccountType.Unspecified)]
    [InlineData(Atm.Grpc.AccountType.Personal)]
    public async Task CreateAccount_WithPersonalOrUnspecifiedType_ShouldCreatePersonalAccount(Atm.Grpc.AccountType requestedType)
    {
        string owner = UniqueId("owner");
        string accountNumber = UniqueId("account");
        Guid adminSession = await SeedAdminSessionAsync();
        await SeedUserAsync(owner);

        await Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = adminSession.ToString(),
            Number = accountNumber,
            PinCode = "1234",
            OwnerUserId = owner,
            AccountType = requestedType,
        });

        Account? created = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(created);
        created.Type.Should().Be(Atm.Domain.AccountType.Personal);
    }

    [Fact]
    public async Task CreateAccount_ShouldWriteExactlyOneEventToOutbox()
    {
        string owner = UniqueId("owner");
        Guid adminSession = await SeedAdminSessionAsync();
        await SeedUserAsync(owner);
        long before = await Grpc.CountOutboxMessagesAsync();

        await Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = adminSession.ToString(),
            Number = UniqueId("account"),
            PinCode = "1234",
            OwnerUserId = owner,
            AccountType = Atm.Grpc.AccountType.Corporate,
        });

        long after = await Grpc.CountOutboxMessagesAsync();
        (after - before).Should().Be(1);
    }

    [Fact]
    public async Task CreateAccount_WhenRejected_ShouldNotWriteAnythingToOutbox()
    {
        string owner = UniqueId("owner");
        await SeedUserAsync(owner);
        long before = await Grpc.CountOutboxMessagesAsync();

        Func<Task> act = () => Grpc.AccountClient.CreateAccountAsync(new CreateAccountRequest
        {
            SessionKey = Guid.NewGuid().ToString(),
            Number = UniqueId("account"),
            PinCode = "1234",
            OwnerUserId = owner,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>();

        long after = await Grpc.CountOutboxMessagesAsync();
        after.Should().Be(before);
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
