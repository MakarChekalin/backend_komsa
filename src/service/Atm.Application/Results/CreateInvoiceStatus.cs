namespace Atm.Application.Results;

public enum CreateInvoiceStatus
{
    Success,
    Unauthorized,
    PayerNotFound,
    InvalidAmount,
}
