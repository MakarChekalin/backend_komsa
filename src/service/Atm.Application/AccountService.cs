using Atm.Application.Results;
using Atm.Domain;
using Itmo.Dev.Platform.Persistence.Abstractions.Transactions;
using Microsoft.Extensions.Logging;
using System.Data;

namespace Atm.Application;

public class AccountService : IAccountService
{
    private const int MaxAccountsPerUser = 5;

    private readonly IAccountRepository _accountRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly IOperationRepository _operationRepository;
    private readonly IUserRepository _userRepository;
    private readonly IPersistenceTransactionProvider _transactionProvider;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        IAccountRepository accountRepository,
        ISessionRepository sessionRepository,
        IOperationRepository operationRepository,
        IUserRepository userRepository,
        IPersistenceTransactionProvider transactionProvider,
        ILogger<AccountService> logger)
    {
        _accountRepository = accountRepository;
        _sessionRepository = sessionRepository;
        _operationRepository = operationRepository;
        _userRepository = userRepository;
        _transactionProvider = transactionProvider;
        _logger = logger;
    }

    public async Task<CreateAccountResult> CreateAccountAsync(Guid sessionKey, string number, string pinCode, string ownerUserId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "AccountService.CreateAccount",
            new Dictionary<string, string?> { ["user_id"] = ownerUserId, ["account_id"] = number });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.Admin)
        {
            AccountServiceLog.CreateAccountNoAdminSession(_logger, number);
            return CreateAccountResult.Unauthorized;
        }

        if (await _accountRepository.ExistsAsync(number))
        {
            AccountServiceLog.CreateAccountAlreadyExists(_logger, number);
            return CreateAccountResult.Exists;
        }

        if (!await _userRepository.ExistsAsync(ownerUserId))
        {
            AccountServiceLog.CreateAccountOwnerNotFound(_logger, ownerUserId);
            return CreateAccountResult.OwnerNotFound;
        }

        if (await _accountRepository.CountByUserIdAsync(ownerUserId) >= MaxAccountsPerUser)
        {
            AccountServiceLog.CreateAccountLimitExceeded(_logger, ownerUserId);
            return CreateAccountResult.AccountLimitExceeded;
        }

        var account = new Account(number, pinCode, ownerUserId, balance: 0);
        await _accountRepository.SaveAsync(account);

        AtmMetrics.AccountsCreated.Add(1);
        AccountServiceLog.AccountCreated(_logger, number, ownerUserId);

        return CreateAccountResult.Success;
    }

    public async Task<WithdrawResult> WithdrawAsync(Guid sessionKey, decimal amount, string accountNumber, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "AccountService.Withdraw",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = accountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
        {
            AccountServiceLog.WithdrawNoUserSession(_logger, accountNumber);
            return WithdrawResult.Unauthorized;
        }

        Account? account = await _accountRepository.GetByNumberAsync(session.AccountNumber);

        if (account == null || account.Number != accountNumber || account.UserId != userId)
        {
            AccountServiceLog.WithdrawNotOwner(_logger, accountNumber, userId);
            return WithdrawResult.Unauthorized;
        }

        if (amount <= 0)
        {
            AccountServiceLog.WithdrawInvalidAmount(_logger, amount, accountNumber);
            return WithdrawResult.InvalidAmount;
        }

        bool success = account.Withdraw(amount);

        if (!success)
        {
            AccountServiceLog.WithdrawInsufficientFunds(_logger, amount, accountNumber);
            return WithdrawResult.InsufficientFunds;
        }

        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        await _accountRepository.SaveAsync(account);
        await _operationRepository.SaveAsync(new Operation(account.Number, OperationType.Withdraw, amount));

        await transaction.CommitAsync(CancellationToken.None);

        AtmMetrics.Withdrawals.Add(1);
        AccountServiceLog.Withdrew(_logger, amount, accountNumber);

        return WithdrawResult.Success;
    }

    public async Task<DepositResult> DepositAsync(Guid sessionKey, decimal amount, string accountNumber, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "AccountService.Deposit",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = accountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
        {
            AccountServiceLog.DepositNoUserSession(_logger, accountNumber);
            return DepositResult.Unauthorized;
        }

        Account? account = await _accountRepository.GetByNumberAsync(session.AccountNumber);

        if (account == null || account.Number != accountNumber || account.UserId != userId)
        {
            AccountServiceLog.DepositNotOwner(_logger, accountNumber, userId);
            return DepositResult.Unauthorized;
        }

        if (amount <= 0)
        {
            AccountServiceLog.DepositInvalidAmount(_logger, amount, accountNumber);
            return DepositResult.InvalidAmount;
        }

        account.Deposit(amount);

        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        await _accountRepository.SaveAsync(account);
        await _operationRepository.SaveAsync(new Operation(account.Number, OperationType.Deposit, amount));

        await transaction.CommitAsync(CancellationToken.None);

        AtmMetrics.Deposits.Add(1);
        AccountServiceLog.Deposited(_logger, amount, accountNumber);

        return DepositResult.Success;
    }

    public async Task<GetBalanceResult> GetBalanceAsync(Guid sessionKey, string accountNumber, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "AccountService.GetBalance",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = accountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
            return new GetBalanceResult(GetBalanceStatus.Unauthorized, 0);

        Account? account = await _accountRepository.GetByNumberAsync(session.AccountNumber);

        if (account == null || account.Number != accountNumber || account.UserId != userId)
            return new GetBalanceResult(GetBalanceStatus.Unauthorized, 0);

        return new GetBalanceResult(GetBalanceStatus.Success, account.Balance);
    }

    public async Task<GetHistoryResult> GetHistoryAsync(Guid sessionKey, string accountNumber, string userId, long? cursor, int pageSize)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "AccountService.GetHistory",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = accountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
            return new GetHistoryResult(GetHistoryStatus.Unauthorized, null, null);

        Account? account = await _accountRepository.GetByNumberAsync(session.AccountNumber);

        if (account == null || account.Number != accountNumber || account.UserId != userId)
            return new GetHistoryResult(GetHistoryStatus.Unauthorized, null, null);

        var operations = new List<Operation>();

        await foreach (Operation operation in _operationRepository.GetByAccountNumberAsync(session.AccountNumber, cursor, pageSize + 1))
            operations.Add(operation);

        long? nextCursor = null;

        if (operations.Count > pageSize)
        {
            nextCursor = operations[pageSize - 1].Id;
            operations.RemoveRange(pageSize, operations.Count - pageSize);
        }

        return new GetHistoryResult(GetHistoryStatus.Success, operations, nextCursor);
    }
}
