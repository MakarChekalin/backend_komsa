namespace Atm.Application.Results;

public enum WithdrawResult
{
    Success,
    Unauthorized,
    InsufficientFunds,
    InvalidAmount,
}
