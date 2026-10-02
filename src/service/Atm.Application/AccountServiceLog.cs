using Microsoft.Extensions.Logging;

namespace Atm.Application;

internal static partial class AccountServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Account creation for {AccountNumber} rejected: no admin session")]
    public static partial void CreateAccountNoAdminSession(ILogger logger, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account creation rejected: account {AccountNumber} already exists")]
    public static partial void CreateAccountAlreadyExists(ILogger logger, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account creation rejected: owner {UserId} not found")]
    public static partial void CreateAccountOwnerNotFound(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account creation rejected: user {UserId} reached the account limit")]
    public static partial void CreateAccountLimitExceeded(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created account {AccountNumber} for user {UserId}")]
    public static partial void AccountCreated(ILogger logger, string accountNumber, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Withdrawal rejected: no user session for account {AccountNumber}")]
    public static partial void WithdrawNoUserSession(ILogger logger, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Withdrawal rejected: account {AccountNumber} does not belong to user {UserId}")]
    public static partial void WithdrawNotOwner(ILogger logger, string accountNumber, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Withdrawal of {Amount} from account {AccountNumber} rejected: insufficient funds")]
    public static partial void WithdrawInsufficientFunds(ILogger logger, decimal amount, string accountNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Withdrew {Amount} from account {AccountNumber}")]
    public static partial void Withdrew(ILogger logger, decimal amount, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deposit rejected: no user session for account {AccountNumber}")]
    public static partial void DepositNoUserSession(ILogger logger, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deposit rejected: account {AccountNumber} does not belong to user {UserId}")]
    public static partial void DepositNotOwner(ILogger logger, string accountNumber, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deposited {Amount} into account {AccountNumber}")]
    public static partial void Deposited(ILogger logger, decimal amount, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Withdrawal of {Amount} from account {AccountNumber} rejected: amount must be positive")]
    public static partial void WithdrawInvalidAmount(ILogger logger, decimal amount, string accountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deposit of {Amount} into account {AccountNumber} rejected: amount must be positive")]
    public static partial void DepositInvalidAmount(ILogger logger, decimal amount, string accountNumber);
}
