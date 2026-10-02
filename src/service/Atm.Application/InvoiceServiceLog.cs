using Microsoft.Extensions.Logging;

namespace Atm.Application;

internal static partial class InvoiceServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice creation rejected: no user session")]
    public static partial void CreateInvoiceNoUserSession(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice creation rejected: payer account {AccountNumber} not found")]
    public static partial void CreateInvoicePayerNotFound(ILogger logger, string accountNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created invoice {InvoiceId} for {Amount} payable by {PayerAccountNumber}")]
    public static partial void InvoiceCreated(ILogger logger, Guid invoiceId, decimal amount, string payerAccountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} payment rejected: no user session")]
    public static partial void PayInvoiceNoUserSession(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} payment rejected: invoice not found")]
    public static partial void PayInvoiceNotFound(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} payment rejected: caller is not the payer")]
    public static partial void PayInvoiceNotPayer(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} payment rejected: already processed")]
    public static partial void PayInvoiceAlreadyProcessed(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} payment rejected: pending corporate approval")]
    public static partial void PayInvoicePendingApproval(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} payment rejected: insufficient funds on {PayerAccountNumber}")]
    public static partial void PayInvoiceInsufficientFunds(ILogger logger, Guid invoiceId, string payerAccountNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paid invoice {InvoiceId}: {PayerAccountNumber} -> {PayeeAccountNumber}")]
    public static partial void InvoicePaid(ILogger logger, Guid invoiceId, string payerAccountNumber, string payeeAccountNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} cancellation rejected: no user session")]
    public static partial void CancelInvoiceNoUserSession(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} cancellation rejected: invoice not found")]
    public static partial void CancelInvoiceNotFound(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} cancellation rejected: caller is not the payee")]
    public static partial void CancelInvoiceNotPayee(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} cancellation rejected: already processed")]
    public static partial void CancelInvoiceAlreadyProcessed(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} cancellation rejected: pending corporate approval")]
    public static partial void CancelInvoicePendingApproval(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancelled invoice {InvoiceId}")]
    public static partial void InvoiceCancelled(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation rejected: account {AccountNumber} does not belong to user {UserId}")]
    public static partial void SessionAccountNotOwned(ILogger logger, string accountNumber, string userId);
}
