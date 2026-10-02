using Atm.Application.Results;
using Atm.Domain;
using Itmo.Dev.Platform.Persistence.Abstractions.Transactions;
using Microsoft.Extensions.Logging;
using System.Data;

namespace Atm.Application;

public class InvoiceService : IInvoiceService
{
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly ISessionRepository _sessionRepository;
    private readonly IOperationRepository _operationRepository;
    private readonly IPersistenceTransactionProvider _transactionProvider;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(
        IInvoiceRepository invoiceRepository,
        IAccountRepository accountRepository,
        ISessionRepository sessionRepository,
        IOperationRepository operationRepository,
        IPersistenceTransactionProvider transactionProvider,
        ILogger<InvoiceService> logger)
    {
        _invoiceRepository = invoiceRepository;
        _accountRepository = accountRepository;
        _sessionRepository = sessionRepository;
        _operationRepository = operationRepository;
        _transactionProvider = transactionProvider;
        _logger = logger;
    }

    public async Task<CreateInvoiceResult> CreateInvoiceAsync(Guid sessionKey, string payerAccountNumber, decimal amount, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "InvoiceService.CreateInvoice",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = payerAccountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
        {
            InvoiceServiceLog.CreateInvoiceNoUserSession(_logger);
            return new CreateInvoiceResult(CreateInvoiceStatus.Unauthorized, null);
        }

        if (await GetOwnedSessionAccountAsync(session.AccountNumber, userId) == null)
            return new CreateInvoiceResult(CreateInvoiceStatus.Unauthorized, null);

        if (amount <= 0)
        {
            InvoiceServiceLog.CreateInvoiceInvalidAmount(_logger, amount);
            return new CreateInvoiceResult(CreateInvoiceStatus.InvalidAmount, null);
        }

        if (!await _accountRepository.ExistsAsync(payerAccountNumber))
        {
            InvoiceServiceLog.CreateInvoicePayerNotFound(_logger, payerAccountNumber);
            return new CreateInvoiceResult(CreateInvoiceStatus.PayerNotFound, null);
        }

        var invoice = new Invoice(payerAccountNumber, session.AccountNumber, amount);
        await _invoiceRepository.SaveAsync(invoice);

        AtmMetrics.InvoicesCreated.Add(1);
        InvoiceServiceLog.InvoiceCreated(_logger, invoice.Id, amount, payerAccountNumber);

        return new CreateInvoiceResult(CreateInvoiceStatus.Success, invoice.Id);
    }

    public async Task<PayInvoiceResult> PayInvoiceAsync(Guid sessionKey, Guid invoiceId, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "InvoiceService.PayInvoice",
            new Dictionary<string, string?> { ["user_id"] = userId, ["invoice_id"] = invoiceId.ToString() });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
        {
            InvoiceServiceLog.PayInvoiceNoUserSession(_logger, invoiceId);
            return PayInvoiceResult.Unauthorized;
        }

        Invoice? invoice = await _invoiceRepository.GetByIdAsync(invoiceId);

        if (invoice == null)
        {
            InvoiceServiceLog.PayInvoiceNotFound(_logger, invoiceId);
            return PayInvoiceResult.NotFound;
        }

        if (invoice.PayerAccountNumber != session.AccountNumber)
        {
            InvoiceServiceLog.PayInvoiceNotPayer(_logger, invoiceId);
            return PayInvoiceResult.Unauthorized;
        }

        Account? payer = await _accountRepository.GetByNumberAsync(invoice.PayerAccountNumber);
        Account? payee = await _accountRepository.GetByNumberAsync(invoice.PayeeAccountNumber);

        if (payer == null || payee == null)
            return PayInvoiceResult.NotFound;

        if (payer.UserId != userId)
        {
            InvoiceServiceLog.SessionAccountNotOwned(_logger, payer.Number, userId);
            return PayInvoiceResult.Unauthorized;
        }

        if (!invoice.CanBePaid)
        {
            InvoiceServiceLog.PayInvoiceAlreadyProcessed(_logger, invoiceId);
            return PayInvoiceResult.AlreadyProcessed;
        }

        if (!payer.Withdraw(invoice.Amount))
        {
            InvoiceServiceLog.PayInvoiceInsufficientFunds(_logger, invoiceId, payer.Number);
            return PayInvoiceResult.InsufficientFunds;
        }

        payee.Deposit(invoice.Amount);
        invoice.Pay();

        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        await _accountRepository.SaveAsync(payer);
        await _accountRepository.SaveAsync(payee);
        await _invoiceRepository.SaveAsync(invoice);

        await _operationRepository.SaveAsync(new Operation(payer.Number, OperationType.Withdraw, invoice.Amount, invoice.Id));
        await _operationRepository.SaveAsync(new Operation(payee.Number, OperationType.Deposit, invoice.Amount, invoice.Id));

        await transaction.CommitAsync(CancellationToken.None);

        AtmMetrics.InvoicesPaid.Add(1);
        InvoiceServiceLog.InvoicePaid(_logger, invoiceId, payer.Number, payee.Number);

        return PayInvoiceResult.Success;
    }

    public async Task<CancelInvoiceResult> CancelInvoiceAsync(Guid sessionKey, Guid invoiceId, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "InvoiceService.CancelInvoice",
            new Dictionary<string, string?> { ["user_id"] = userId, ["invoice_id"] = invoiceId.ToString() });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
        {
            InvoiceServiceLog.CancelInvoiceNoUserSession(_logger, invoiceId);
            return CancelInvoiceResult.Unauthorized;
        }

        Invoice? invoice = await _invoiceRepository.GetByIdAsync(invoiceId);

        if (invoice == null)
        {
            InvoiceServiceLog.CancelInvoiceNotFound(_logger, invoiceId);
            return CancelInvoiceResult.NotFound;
        }

        if (invoice.PayeeAccountNumber != session.AccountNumber)
        {
            InvoiceServiceLog.CancelInvoiceNotPayee(_logger, invoiceId);
            return CancelInvoiceResult.Unauthorized;
        }

        if (await GetOwnedSessionAccountAsync(session.AccountNumber, userId) == null)
            return CancelInvoiceResult.Unauthorized;

        if (!invoice.Cancel())
        {
            InvoiceServiceLog.CancelInvoiceAlreadyProcessed(_logger, invoiceId);
            return CancelInvoiceResult.AlreadyProcessed;
        }

        await _invoiceRepository.SaveAsync(invoice);

        AtmMetrics.InvoicesCancelled.Add(1);
        InvoiceServiceLog.InvoiceCancelled(_logger, invoiceId);

        return CancelInvoiceResult.Success;
    }

    public async Task<GetOutgoingInvoicesResult> GetOutgoingInvoicesAsync(Guid sessionKey, string? payerAccountNumber, InvoiceStatus? status, Guid? cursor, int pageSize, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "InvoiceService.GetOutgoingInvoices",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = payerAccountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
            return new GetOutgoingInvoicesResult(GetInvoicesStatus.Unauthorized, null, null);

        if (await GetOwnedSessionAccountAsync(session.AccountNumber, userId) == null)
            return new GetOutgoingInvoicesResult(GetInvoicesStatus.Unauthorized, null, null);

        var invoices = new List<Invoice>();

        await foreach (Invoice invoice in _invoiceRepository.GetOutgoingAsync(session.AccountNumber, payerAccountNumber, status, cursor, pageSize + 1))
            invoices.Add(invoice);

        Guid? nextCursor = null;

        if (invoices.Count > pageSize)
        {
            nextCursor = invoices[pageSize - 1].Id;
            invoices.RemoveRange(pageSize, invoices.Count - pageSize);
        }

        return new GetOutgoingInvoicesResult(GetInvoicesStatus.Success, invoices, nextCursor);
    }

    public async Task<GetIncomingInvoicesResult> GetIncomingInvoicesAsync(Guid sessionKey, string? payeeAccountNumber, InvoiceStatus? status, Guid? cursor, int pageSize, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "InvoiceService.GetIncomingInvoices",
            new Dictionary<string, string?> { ["user_id"] = userId, ["account_id"] = payeeAccountNumber });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
            return new GetIncomingInvoicesResult(GetInvoicesStatus.Unauthorized, null, null);

        if (await GetOwnedSessionAccountAsync(session.AccountNumber, userId) == null)
            return new GetIncomingInvoicesResult(GetInvoicesStatus.Unauthorized, null, null);

        var invoices = new List<Invoice>();

        await foreach (Invoice invoice in _invoiceRepository.GetIncomingAsync(session.AccountNumber, payeeAccountNumber, status, cursor, pageSize + 1))
            invoices.Add(invoice);

        Guid? nextCursor = null;

        if (invoices.Count > pageSize)
        {
            nextCursor = invoices[pageSize - 1].Id;
            invoices.RemoveRange(pageSize, invoices.Count - pageSize);
        }

        return new GetIncomingInvoicesResult(GetInvoicesStatus.Success, invoices, nextCursor);
    }

    private async Task<Account?> GetOwnedSessionAccountAsync(string accountNumber, string userId)
    {
        Account? account = await _accountRepository.GetByNumberAsync(accountNumber);

        if (account == null || account.UserId != userId)
        {
            InvoiceServiceLog.SessionAccountNotOwned(_logger, accountNumber, userId);
            return null;
        }

        return account;
    }
}
