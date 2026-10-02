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
    private readonly IUserRepository _userRepository;
    private readonly IInvoiceEventPublisher _invoiceEventPublisher;
    private readonly IInvoiceApprovalClient _invoiceApprovalClient;
    private readonly IPersistenceTransactionProvider _transactionProvider;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(
        IInvoiceRepository invoiceRepository,
        IAccountRepository accountRepository,
        ISessionRepository sessionRepository,
        IOperationRepository operationRepository,
        IUserRepository userRepository,
        IInvoiceEventPublisher invoiceEventPublisher,
        IInvoiceApprovalClient invoiceApprovalClient,
        IPersistenceTransactionProvider transactionProvider,
        ILogger<InvoiceService> logger)
    {
        _invoiceRepository = invoiceRepository;
        _accountRepository = accountRepository;
        _sessionRepository = sessionRepository;
        _operationRepository = operationRepository;
        _userRepository = userRepository;
        _invoiceEventPublisher = invoiceEventPublisher;
        _invoiceApprovalClient = invoiceApprovalClient;
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

        Account? payee = await GetOwnedSessionAccountAsync(session.AccountNumber, userId);

        if (payee == null)
            return new CreateInvoiceResult(CreateInvoiceStatus.Unauthorized, null);

        Account? payer = await _accountRepository.GetByNumberAsync(payerAccountNumber);

        if (payer == null)
        {
            InvoiceServiceLog.CreateInvoicePayerNotFound(_logger, payerAccountNumber);
            return new CreateInvoiceResult(CreateInvoiceStatus.PayerNotFound, null);
        }

        var invoice = new Invoice(payerAccountNumber, session.AccountNumber, amount);

        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        long externalId = await _invoiceRepository.SaveAsync(invoice);

        await _invoiceEventPublisher.PublishInvoiceCreatedAsync(
            externalId,
            payee.ExternalId,
            payer.ExternalId,
            amount,
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

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

        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        Invoice? invoice = await _invoiceRepository.GetByIdForUpdateAsync(invoiceId);

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

        (Account? payer, Account? payee) = await LockAccountsAsync(invoice.PayerAccountNumber, invoice.PayeeAccountNumber);

        if (payer == null || payee == null)
            return PayInvoiceResult.NotFound;

        if (payer.UserId != userId)
        {
            InvoiceServiceLog.SessionAccountNotOwned(_logger, payer.Number, userId);
            return PayInvoiceResult.Unauthorized;
        }

        if (payer.Type == AccountType.Corporate && invoice.Status == InvoiceStatus.Created)
        {
            InvoiceServiceLog.PayInvoicePendingApproval(_logger, invoiceId);
            return PayInvoiceResult.PendingApproval;
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

        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        Invoice? invoice = await _invoiceRepository.GetByIdForUpdateAsync(invoiceId);

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

        Account? payee = await GetOwnedSessionAccountAsync(session.AccountNumber, userId);

        if (payee == null)
            return CancelInvoiceResult.Unauthorized;

        Account? payer = await _accountRepository.GetByNumberAsync(invoice.PayerAccountNumber);

        if (payer == null)
        {
            InvoiceServiceLog.CancelInvoiceNotFound(_logger, invoiceId);
            return CancelInvoiceResult.NotFound;
        }

        if (payer.Type == AccountType.Corporate && invoice.Status == InvoiceStatus.Created)
        {
            InvoiceServiceLog.CancelInvoicePendingApproval(_logger, invoiceId);
            return CancelInvoiceResult.PendingApproval;
        }

        if (!invoice.Cancel())
        {
            InvoiceServiceLog.CancelInvoiceAlreadyProcessed(_logger, invoiceId);
            return CancelInvoiceResult.AlreadyProcessed;
        }

        await _invoiceRepository.SaveAsync(invoice);

        await transaction.CommitAsync(CancellationToken.None);

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

    public async Task<AssignAccountantResult> AssignAccountantAsync(Guid sessionKey, Guid invoiceId, string accountantUserId, string userId)
    {
        using IDisposable scope = OperationScope.Begin(
            _logger,
            "InvoiceService.AssignAccountant",
            new Dictionary<string, string?> { ["user_id"] = userId, ["invoice_id"] = invoiceId.ToString() });

        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
            return AssignAccountantResult.Unauthorized;

        if (await GetOwnedSessionAccountAsync(session.AccountNumber, userId) == null)
            return AssignAccountantResult.Unauthorized;

        Invoice? invoice = await _invoiceRepository.GetByIdAsync(invoiceId);

        if (invoice == null)
            return AssignAccountantResult.NotFound;

        if (invoice.PayeeAccountNumber != session.AccountNumber)
            return AssignAccountantResult.Unauthorized;

        long? accountantExternalId = await _userRepository.GetExternalIdAsync(accountantUserId);

        if (accountantExternalId == null)
            return AssignAccountantResult.AccountantNotFound;

        InvoiceApprovalCallResult result = await _invoiceApprovalClient.AssignAccountantAsync(
            invoice.ExternalId,
            accountantExternalId.Value,
            CancellationToken.None);

        return result switch
        {
            InvoiceApprovalCallResult.Success => AssignAccountantResult.Success,
            InvoiceApprovalCallResult.NotFound => AssignAccountantResult.NotFound,
            InvoiceApprovalCallResult.InvalidState => AssignAccountantResult.InvalidState,
            InvoiceApprovalCallResult.ServiceUnavailable => AssignAccountantResult.ServiceUnavailable,
            _ => AssignAccountantResult.ServiceUnavailable,
        };
    }

    public async Task<AccountantActionResult> ApproveInvoiceAsync(Guid sessionKey, Guid invoiceId, string userId)
    {
        return await RunAccountantActionAsync(sessionKey, invoiceId, userId, _invoiceApprovalClient.ApproveInvoiceAsync);
    }

    public async Task<AccountantActionResult> DeclineInvoiceAsync(Guid sessionKey, Guid invoiceId, string userId)
    {
        return await RunAccountantActionAsync(sessionKey, invoiceId, userId, _invoiceApprovalClient.DeclineInvoiceAsync);
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

    private async Task<(Account? Payer, Account? Payee)> LockAccountsAsync(string payerNumber, string payeeNumber)
    {
        if (payerNumber == payeeNumber)
        {
            Account? same = await _accountRepository.GetByNumberForUpdateAsync(payerNumber);
            return (same, same);
        }

        bool payerFirst = string.CompareOrdinal(payerNumber, payeeNumber) < 0;

        Account? first = await _accountRepository.GetByNumberForUpdateAsync(payerFirst ? payerNumber : payeeNumber);
        Account? second = await _accountRepository.GetByNumberForUpdateAsync(payerFirst ? payeeNumber : payerNumber);

        return payerFirst ? (first, second) : (second, first);
    }

    private async Task<AccountantActionResult> RunAccountantActionAsync(
        Guid sessionKey,
        Guid invoiceId,
        string userId,
        Func<long, long, CancellationToken, Task<InvoiceApprovalCallResult>> action)
    {
        Session? session = await _sessionRepository.GetByKeyAsync(sessionKey);

        if (session == null || session.Type != SessionType.User || session.AccountNumber == null)
            return AccountantActionResult.Unauthorized;

        if (await GetOwnedSessionAccountAsync(session.AccountNumber, userId) == null)
            return AccountantActionResult.Unauthorized;

        Invoice? invoice = await _invoiceRepository.GetByIdAsync(invoiceId);

        if (invoice == null)
            return AccountantActionResult.NotFound;

        long? accountantExternalId = await _userRepository.GetExternalIdAsync(userId);

        if (accountantExternalId == null)
            return AccountantActionResult.Unauthorized;

        InvoiceApprovalCallResult result = await action(invoice.ExternalId, accountantExternalId.Value, CancellationToken.None);

        return result switch
        {
            InvoiceApprovalCallResult.Success => AccountantActionResult.Success,
            InvoiceApprovalCallResult.NotFound => AccountantActionResult.NotFound,
            InvoiceApprovalCallResult.InvalidState => AccountantActionResult.InvalidState,
            InvoiceApprovalCallResult.ServiceUnavailable => AccountantActionResult.ServiceUnavailable,
            _ => AccountantActionResult.ServiceUnavailable,
        };
    }
}
