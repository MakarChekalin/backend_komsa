using Atm.Application;
using Atm.Application.Results;
using Atm.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Atm.Tests;

public class AccountServiceTests
{
    private readonly Mock<IAccountRepository> _accountRepository = new();
    private readonly Mock<ISessionRepository> _sessionRepository = new();
    private readonly Mock<IOperationRepository> _operationRepository = new();
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IAccountEventPublisher> _accountEventPublisher = new();

    [Fact]
    public async Task CreateAccount_WithValidAdminSessionAndKnownOwner_ShouldSucceed()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(false);
        _userRepository.Setup(r => r.ExistsAsync("user-1")).ReturnsAsync(true);
        _accountRepository.Setup(r => r.CountByUserIdAsync("user-1")).ReturnsAsync(0);

        AccountService service = CreateService();

        CreateAccountResult result = await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", AccountType.Personal);

        result.Should().Be(CreateAccountResult.Success);
        _accountRepository.Verify(r => r.SaveAsync(It.Is<Account>(a => a.Number == "40817123" && a.UserId == "user-1")), Times.Once);
    }

    [Fact]
    public async Task CreateAccount_ShouldSaveAccountWithRequestedType()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(false);
        _userRepository.Setup(r => r.ExistsAsync("user-1")).ReturnsAsync(true);
        _accountRepository.Setup(r => r.CountByUserIdAsync("user-1")).ReturnsAsync(0);

        AccountService service = CreateService();

        await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", AccountType.Corporate);

        _accountRepository.Verify(r => r.SaveAsync(It.Is<Account>(a => a.Type == AccountType.Corporate)), Times.Once);
    }

    [Theory]
    [InlineData(AccountType.Personal)]
    [InlineData(AccountType.Corporate)]
    public async Task CreateAccount_ShouldPublishEventWithExternalIdsAndType(AccountType type)
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(false);
        _userRepository.Setup(r => r.ExistsAsync("user-1")).ReturnsAsync(true);
        _userRepository.Setup(r => r.GetExternalIdAsync("user-1")).ReturnsAsync(5);
        _accountRepository.Setup(r => r.CountByUserIdAsync("user-1")).ReturnsAsync(0);
        _accountRepository.Setup(r => r.SaveAsync(It.IsAny<Account>())).ReturnsAsync(77);

        AccountService service = CreateService();

        await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", type);

        _accountEventPublisher.Verify(
            p => p.PublishAccountCreatedAsync(5, 77, type, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAccount_WhenRejected_ShouldNotPublishEvent()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(true);

        AccountService service = CreateService();

        await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", AccountType.Corporate);

        _accountEventPublisher.Verify(
            p => p.PublishAccountCreatedAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<AccountType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAccount_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        AccountService service = CreateService();

        CreateAccountResult result = await service.CreateAccountAsync(Guid.NewGuid(), "40817123", "1234", "user-1", AccountType.Personal);

        result.Should().Be(CreateAccountResult.Unauthorized);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task CreateAccount_WithUserSessionInsteadOfAdmin_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "40817000");
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        CreateAccountResult result = await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", AccountType.Personal);

        result.Should().Be(CreateAccountResult.Unauthorized);
    }

    [Fact]
    public async Task CreateAccount_WithAlreadyExistingNumber_ShouldReturnExists()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(true);

        AccountService service = CreateService();

        CreateAccountResult result = await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", AccountType.Personal);

        result.Should().Be(CreateAccountResult.Exists);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task CreateAccount_WithUnknownOwner_ShouldReturnOwnerNotFound()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(false);
        _userRepository.Setup(r => r.ExistsAsync("ghost")).ReturnsAsync(false);

        AccountService service = CreateService();

        CreateAccountResult result = await service.CreateAccountAsync(session.Key, "40817123", "1234", "ghost", AccountType.Personal);

        result.Should().Be(CreateAccountResult.OwnerNotFound);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task CreateAccount_WhenOwnerReachedAccountLimit_ShouldReturnAccountLimitExceeded()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.ExistsAsync("40817123")).ReturnsAsync(false);
        _userRepository.Setup(r => r.ExistsAsync("user-1")).ReturnsAsync(true);
        _accountRepository.Setup(r => r.CountByUserIdAsync("user-1")).ReturnsAsync(5);

        AccountService service = CreateService();

        CreateAccountResult result = await service.CreateAccountAsync(session.Key, "40817123", "1234", "user-1", AccountType.Personal);

        result.Should().Be(CreateAccountResult.AccountLimitExceeded);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task Withdraw_WithSufficientBalance_ShouldUpdateBalance()
    {
        var account = new Account("40817123", "1234", "user-1", 1000);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        WithdrawResult result = await service.WithdrawAsync(session.Key, 300, "40817123", "user-1");

        result.Should().Be(WithdrawResult.Success);
        account.Balance.Should().Be(700);
        _accountRepository.Verify(r => r.SaveAsync(account), Times.Once);
    }

    [Fact]
    public async Task Withdraw_WithInsufficientBalance_ShouldReturnError()
    {
        var account = new Account("40817123", "1234", "user-1", 100);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        WithdrawResult result = await service.WithdrawAsync(session.Key, 300, "40817123", "user-1");

        result.Should().Be(WithdrawResult.InsufficientFunds);
        account.Balance.Should().Be(100);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task Withdraw_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        AccountService service = CreateService();

        WithdrawResult result = await service.WithdrawAsync(Guid.NewGuid(), 100, "40817123", "user-1");

        result.Should().Be(WithdrawResult.Unauthorized);
    }

    [Fact]
    public async Task Withdraw_WithAdminSession_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.Admin);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        WithdrawResult result = await service.WithdrawAsync(session.Key, 100, "40817123", "user-1");

        result.Should().Be(WithdrawResult.Unauthorized);
    }

    [Fact]
    public async Task Withdraw_WithAccountNumberNotMatchingSession_ShouldReturnUnauthorized()
    {
        var account = new Account("40817123", "1234", "user-1", 1000);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        WithdrawResult result = await service.WithdrawAsync(session.Key, 100, "someone-elses-account", "user-1");

        result.Should().Be(WithdrawResult.Unauthorized);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task Withdraw_WithUserIdNotOwningAccount_ShouldReturnUnauthorized()
    {
        var account = new Account("40817123", "1234", "user-1", 1000);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        WithdrawResult result = await service.WithdrawAsync(session.Key, 100, "40817123", "someone-else");

        result.Should().Be(WithdrawResult.Unauthorized);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task Deposit_ShouldUpdateBalance()
    {
        var account = new Account("40817123", "1234", "user-1", 500);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        DepositResult result = await service.DepositAsync(session.Key, 250, "40817123", "user-1");

        result.Should().Be(DepositResult.Success);
        account.Balance.Should().Be(750);
        _accountRepository.Verify(r => r.SaveAsync(account), Times.Once);
    }

    [Fact]
    public async Task Deposit_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        AccountService service = CreateService();

        DepositResult result = await service.DepositAsync(Guid.NewGuid(), 250, "40817123", "user-1");

        result.Should().Be(DepositResult.Unauthorized);
    }

    [Fact]
    public async Task Deposit_WithAccountNumberNotMatchingSession_ShouldReturnUnauthorized()
    {
        var account = new Account("40817123", "1234", "user-1", 500);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        DepositResult result = await service.DepositAsync(session.Key, 250, "40817999", "user-1");

        result.Should().Be(DepositResult.Unauthorized);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
    }

    [Fact]
    public async Task GetBalance_WithValidSession_ShouldReturnBalance()
    {
        var account = new Account("40817123", "1234", "user-1", 1500);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        AccountService service = CreateService();

        GetBalanceResult result = await service.GetBalanceAsync(session.Key, "40817123", "user-1");

        result.Status.Should().Be(GetBalanceStatus.Success);
        result.Balance.Should().Be(1500);
    }

    [Fact]
    public async Task GetBalance_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        AccountService service = CreateService();

        GetBalanceResult result = await service.GetBalanceAsync(Guid.NewGuid(), "40817123", "user-1");

        result.Status.Should().Be(GetBalanceStatus.Unauthorized);
    }

    [Fact]
    public async Task GetBalance_WithUnknownAccountBoundToSession_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "40817123");
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync((Account?)null);

        AccountService service = CreateService();

        GetBalanceResult result = await service.GetBalanceAsync(session.Key, "40817123", "user-1");

        result.Status.Should().Be(GetBalanceStatus.Unauthorized);
    }

    [Fact]
    public async Task GetHistory_WithMoreOperationsThanPageSize_ShouldReturnNextCursor()
    {
        var account = new Account("40817123", "1234", "user-1", 1000);
        var session = new Session(SessionType.User, "40817123");

        _accountRepository.Setup(r => r.GetByNumberAsync("40817123")).ReturnsAsync(account);
        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);

        Operation[] operations =
        [
            new Operation("40817123", OperationType.Deposit, 100, id: 1),
            new Operation("40817123", OperationType.Deposit, 200, id: 2),
            new Operation("40817123", OperationType.Deposit, 300, id: 3),
        ];

        _operationRepository
            .Setup(r => r.GetByAccountNumberAsync("40817123", null, 3))
            .Returns(ToAsyncEnumerable(operations));

        AccountService service = CreateService();

        GetHistoryResult result = await service.GetHistoryAsync(session.Key, "40817123", "user-1", cursor: null, pageSize: 2);

        result.Status.Should().Be(GetHistoryStatus.Success);
        result.Operations.Should().HaveCount(2);
        result.NextCursor.Should().Be(2);
    }

    [Fact]
    public async Task GetHistory_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        AccountService service = CreateService();

        GetHistoryResult result = await service.GetHistoryAsync(Guid.NewGuid(), "40817123", "user-1", null, 20);

        result.Status.Should().Be(GetHistoryStatus.Unauthorized);
        result.Operations.Should().BeNull();
    }

    private static async IAsyncEnumerable<Operation> ToAsyncEnumerable(IEnumerable<Operation> operations)
    {
        foreach (Operation operation in operations)
            yield return operation;

        await Task.CompletedTask;
    }

    private AccountService CreateService()
    {
        return new AccountService(
            _accountRepository.Object,
            _sessionRepository.Object,
            _operationRepository.Object,
            _userRepository.Object,
            _accountEventPublisher.Object,
            TransactionProviderMock.Create(),
            Mock.Of<ILogger<AccountService>>());
    }
}
