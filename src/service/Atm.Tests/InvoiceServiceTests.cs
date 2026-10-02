using Atm.Application;
using Atm.Application.Results;
using Atm.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Atm.Tests;

public class InvoiceServiceTests
{
    private readonly Mock<IInvoiceRepository> _invoiceRepository = new();
    private readonly Mock<IAccountRepository> _accountRepository = new();
    private readonly Mock<ISessionRepository> _sessionRepository = new();
    private readonly Mock<IOperationRepository> _operationRepository = new();
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IInvoiceEventPublisher> _invoiceEventPublisher = new();
    private readonly Mock<IInvoiceApprovalClient> _invoiceApprovalClient = new();

    [Fact]
    public async Task CreateInvoice_WithExistingPayer_ShouldSucceed()
    {
        var session = new Session(SessionType.User, "40817001");
        var payee = new Account("40817001", "1234", "user-payee", 0, externalId: 1);
        var payer = new Account("40817002", "1234", "user-payer", 0, externalId: 2);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.GetByNumberAsync("40817002")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("40817001")).ReturnsAsync(payee);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "40817002", 150, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.Success);
        result.InvoiceId.Should().NotBeNull();
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Once);
    }

    [Fact]
    public async Task CreateInvoice_ShouldPublishEventWithExternalIdsOfInvoiceAndBothAccounts()
    {
        var session = new Session(SessionType.User, "payee-1");
        var payee = new Account("payee-1", "1234", "user-payee", 0, externalId: 11);
        var payer = new Account("payer-1", "1234", "user-payer", 0, externalId: 22);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);
        _invoiceRepository.Setup(r => r.SaveAsync(It.IsAny<Invoice>())).ReturnsAsync(99);

        InvoiceService service = CreateService();

        await service.CreateInvoiceAsync(session.Key, "payer-1", 150, "user-payee");

        _invoiceEventPublisher.Verify(
            p => p.PublishInvoiceCreatedAsync(99, 11, 22, 150, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateInvoice_WithUnknownPayer_ShouldReturnPayerNotFound()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync((Account?)null);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "payer-1", 150, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.PayerNotFound);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Fact]
    public async Task CreateInvoice_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(Guid.NewGuid(), "payer-1", 150, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.Unauthorized);
    }

    [Fact]
    public async Task CreateInvoice_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "someone-else");
        SetupOwnedAccount("payer-1", "user-payer");

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "payer-1", 150, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.Unauthorized);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
        _invoiceEventPublisher.Verify(
            p => p.PublishInvoiceCreatedAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PayInvoice_WithSufficientBalance_ShouldUpdateBothAccountsAndRecordTwoOperations()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.Success);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        payer.Balance.Should().Be(850);
        payee.Balance.Should().Be(150);
        _operationRepository.Verify(r => r.SaveAsync(It.Is<Operation>(o => o.AccountNumber == "payer-1" && o.Type == OperationType.Withdraw)), Times.Once);
        _operationRepository.Verify(r => r.SaveAsync(It.Is<Operation>(o => o.AccountNumber == "payee-1" && o.Type == OperationType.Deposit)), Times.Once);
    }

    [Fact]
    public async Task PayInvoice_ShouldLockInvoiceAndBothAccountsForUpdate()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        _invoiceRepository.Verify(r => r.GetByIdForUpdateAsync(invoice.Id), Times.Once);
        _invoiceRepository.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        _accountRepository.Verify(r => r.GetByNumberForUpdateAsync("payer-1"), Times.Once);
        _accountRepository.Verify(r => r.GetByNumberForUpdateAsync("payee-1"), Times.Once);
    }

    [Fact]
    public async Task PayInvoice_WhenPayerAndPayeeAreTheSameAccount_ShouldNotChangeBalance()
    {
        var account = new Account("acc-1", "1111", "user-1", 1000);
        var session = new Session(SessionType.User, "acc-1");
        var invoice = new Invoice("acc-1", "acc-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberForUpdateAsync("acc-1")).ReturnsAsync(account);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-1");

        result.Should().Be(PayInvoiceResult.Success);
        account.Balance.Should().Be(1000);
    }

    [Fact]
    public async Task PayInvoice_WhenNotThePayer_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "someone-else");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.Unauthorized);
        invoice.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task PayInvoice_WhenPayerAccountBelongsToAnotherUser_ShouldReturnUnauthorized()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "intruder-user");

        result.Should().Be(PayInvoiceResult.Unauthorized);
        invoice.Status.Should().Be(InvoiceStatus.Created);
        payer.Balance.Should().Be(1000);
        payee.Balance.Should().Be(0);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoice_WhenAlreadyPaid_ShouldReturnAlreadyProcessed()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Pay();

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.AlreadyProcessed);
        payer.Balance.Should().Be(1000);
        _operationRepository.Verify(r => r.SaveAsync(It.IsAny<Operation>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoice_WithInsufficientFunds_ShouldReturnInsufficientFundsAndKeepInvoiceUnpaid()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 50);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.InsufficientFunds);
        invoice.Status.Should().Be(InvoiceStatus.Created);
        payer.Balance.Should().Be(50);
        payee.Balance.Should().Be(0);
        _operationRepository.Verify(r => r.SaveAsync(It.IsAny<Operation>()), Times.Never);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoice_WithInsufficientFunds_ThenRetryAfterTopUp_ShouldSucceed()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 50);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult first = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");
        payer.Deposit(500);
        PayInvoiceResult second = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        first.Should().Be(PayInvoiceResult.InsufficientFunds);
        second.Should().Be(PayInvoiceResult.Success);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task PayInvoice_WithUnknownInvoiceId_ShouldReturnNotFound()
    {
        var session = new Session(SessionType.User, "payer-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(It.IsAny<Guid>())).ReturnsAsync((Invoice?)null);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, Guid.NewGuid(), "user-payer");

        result.Should().Be(PayInvoiceResult.NotFound);
    }

    [Fact]
    public async Task PayInvoice_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(Guid.NewGuid(), Guid.NewGuid(), "user-payer");

        result.Should().Be(PayInvoiceResult.Unauthorized);
    }

    [Fact]
    public async Task CancelInvoice_ByIssuer_ShouldSucceed()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        var payer = new Account("payer-1", "1234", "user-payer", 1000);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.Success);
        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
        _invoiceRepository.Verify(r => r.SaveAsync(invoice), Times.Once);
        _invoiceRepository.Verify(r => r.GetByIdForUpdateAsync(invoice.Id), Times.Once);
    }

    [Fact]
    public async Task CancelInvoice_ByPayer_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.Unauthorized);
        invoice.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task CancelInvoice_WhenPayeeAccountBelongsToAnotherUser_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "someone-else");
        SetupOwnedAccount("payer-1", "user-payer");

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.Unauthorized);
        invoice.Status.Should().Be(InvoiceStatus.Created);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Fact]
    public async Task CancelInvoice_WhenAlreadyCancelled_ShouldReturnAlreadyProcessed()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Cancel();
        var payer = new Account("payer-1", "1234", "user-payer", 1000);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.AlreadyProcessed);
    }

    [Fact]
    public async Task CancelInvoice_WithUnknownInvoiceId_ShouldReturnNotFound()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(It.IsAny<Guid>())).ReturnsAsync((Invoice?)null);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, Guid.NewGuid(), "user-payee");

        result.Should().Be(CancelInvoiceResult.NotFound);
    }

    [Fact]
    public async Task CancelInvoice_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(Guid.NewGuid(), Guid.NewGuid(), "user-payee");

        result.Should().Be(CancelInvoiceResult.Unauthorized);
    }

    [Fact]
    public async Task PayInvoice_ForCorporatePayerNotYetApproved_ShouldReturnPendingApproval()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000, AccountType.Corporate);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.PendingApproval);
        invoice.Status.Should().Be(InvoiceStatus.Created);
        payer.Balance.Should().Be(1000);
    }

    [Fact]
    public async Task PayInvoice_ForApprovedCorporatePayer_ShouldSucceed()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000, AccountType.Corporate);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Approve();

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.Success);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        payer.Balance.Should().Be(850);
    }

    [Fact]
    public async Task PayInvoice_WhenDeclined_ShouldReturnAlreadyProcessed()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000, AccountType.Corporate);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Decline();

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupLockedPayment(invoice, payer, payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.AlreadyProcessed);
        payer.Balance.Should().Be(1000);
    }

    [Fact]
    public async Task CancelInvoice_ForCorporatePayerNotYetApproved_ShouldReturnPendingApproval()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        var payer = new Account("payer-1", "1111", "user-payer", 1000, AccountType.Corporate);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.PendingApproval);
        invoice.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task CancelInvoice_ForApprovedCorporatePayer_ShouldSucceed()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Approve();
        var payer = new Account("payer-1", "1111", "user-payer", 1000, AccountType.Corporate);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.Success);
        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
    }

    [Fact]
    public async Task GetOutgoingInvoices_WithMoreThanPageSize_ShouldReturnNextCursor()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");

        Invoice[] invoices =
        [
            new Invoice("payer-1", "payee-1", 10),
            new Invoice("payer-1", "payee-1", 20),
            new Invoice("payer-1", "payee-1", 30),
        ];

        _invoiceRepository
            .Setup(r => r.GetOutgoingAsync("payee-1", null, null, null, 3))
            .Returns(ToAsyncEnumerable(invoices));

        InvoiceService service = CreateService();

        GetOutgoingInvoicesResult result = await service.GetOutgoingInvoicesAsync(session.Key, null, null, null, 2, "user-payee");

        result.Status.Should().Be(GetInvoicesStatus.Success);
        result.Invoices.Should().HaveCount(2);
        result.NextCursor.Should().Be(invoices[1].Id);
    }

    [Fact]
    public async Task GetOutgoingInvoices_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        InvoiceService service = CreateService();

        GetOutgoingInvoicesResult result = await service.GetOutgoingInvoicesAsync(Guid.NewGuid(), null, null, null, 20, "user-payee");

        result.Status.Should().Be(GetInvoicesStatus.Unauthorized);
        result.Invoices.Should().BeNull();
    }

    [Fact]
    public async Task GetOutgoingInvoices_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "someone-else");

        InvoiceService service = CreateService();

        GetOutgoingInvoicesResult result = await service.GetOutgoingInvoicesAsync(session.Key, null, null, null, 20, "user-payee");

        result.Status.Should().Be(GetInvoicesStatus.Unauthorized);
        result.Invoices.Should().BeNull();
        _invoiceRepository.Verify(
            r => r.GetOutgoingAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InvoiceStatus?>(), It.IsAny<Guid?>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task GetIncomingInvoices_WithFewerThanPageSize_ShouldReturnNoNextCursor()
    {
        var session = new Session(SessionType.User, "payer-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payer-1", "user-payer");

        Invoice[] invoices = [new Invoice("payer-1", "payee-1", 10)];

        _invoiceRepository
            .Setup(r => r.GetIncomingAsync("payer-1", null, null, null, 21))
            .Returns(ToAsyncEnumerable(invoices));

        InvoiceService service = CreateService();

        GetIncomingInvoicesResult result = await service.GetIncomingInvoicesAsync(session.Key, null, null, null, 20, "user-payer");

        result.Status.Should().Be(GetInvoicesStatus.Success);
        result.Invoices.Should().HaveCount(1);
        result.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task GetIncomingInvoices_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        InvoiceService service = CreateService();

        GetIncomingInvoicesResult result = await service.GetIncomingInvoicesAsync(Guid.NewGuid(), null, null, null, 20, "user-payer");

        result.Status.Should().Be(GetInvoicesStatus.Unauthorized);
    }

    [Fact]
    public async Task GetIncomingInvoices_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "payer-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payer-1", "someone-else");

        InvoiceService service = CreateService();

        GetIncomingInvoicesResult result = await service.GetIncomingInvoicesAsync(session.Key, null, null, null, 20, "user-payer");

        result.Status.Should().Be(GetInvoicesStatus.Unauthorized);
        result.Invoices.Should().BeNull();
    }

    [Fact]
    public async Task AssignAccountant_ByIssuer_ShouldCallApprovalClientWithExternalIds()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("accountant-1")).ReturnsAsync(7);
        _invoiceApprovalClient
            .Setup(c => c.AssignAccountantAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InvoiceApprovalCallResult.Success);

        InvoiceService service = CreateService();

        AssignAccountantResult result = await service.AssignAccountantAsync(session.Key, invoice.Id, "accountant-1", "user-payee");

        result.Should().Be(AssignAccountantResult.Success);
    }

    [Theory]
    [InlineData(InvoiceApprovalCallResult.NotFound, AssignAccountantResult.NotFound)]
    [InlineData(InvoiceApprovalCallResult.InvalidState, AssignAccountantResult.InvalidState)]
    [InlineData(InvoiceApprovalCallResult.ServiceUnavailable, AssignAccountantResult.ServiceUnavailable)]
    public async Task AssignAccountant_ShouldMapApprovalClientFailures(InvoiceApprovalCallResult clientResult, AssignAccountantResult expected)
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("accountant-1")).ReturnsAsync(7);
        _invoiceApprovalClient
            .Setup(c => c.AssignAccountantAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientResult);

        InvoiceService service = CreateService();

        AssignAccountantResult result = await service.AssignAccountantAsync(session.Key, invoice.Id, "accountant-1", "user-payee");

        result.Should().Be(expected);
    }

    [Fact]
    public async Task AssignAccountant_ByNonIssuer_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "someone-else");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("someone-else", "user-payee");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);

        InvoiceService service = CreateService();

        AssignAccountantResult result = await service.AssignAccountantAsync(session.Key, invoice.Id, "accountant-1", "user-payee");

        result.Should().Be(AssignAccountantResult.Unauthorized);
    }

    [Fact]
    public async Task AssignAccountant_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorizedWithoutCallingApprovalService()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "someone-else");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("accountant-1")).ReturnsAsync(7);

        InvoiceService service = CreateService();

        AssignAccountantResult result = await service.AssignAccountantAsync(session.Key, invoice.Id, "accountant-1", "user-payee");

        result.Should().Be(AssignAccountantResult.Unauthorized);
        _invoiceApprovalClient.Verify(
            c => c.AssignAccountantAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AssignAccountant_WithUnprovisionedAccountant_ShouldReturnAccountantNotFound()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("ghost")).ReturnsAsync((long?)null);

        InvoiceService service = CreateService();

        AssignAccountantResult result = await service.AssignAccountantAsync(session.Key, invoice.Id, "ghost", "user-payee");

        result.Should().Be(AssignAccountantResult.AccountantNotFound);
    }

    [Fact]
    public async Task AssignAccountant_WithUnknownInvoice_ShouldReturnNotFound()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _invoiceRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Invoice?)null);

        InvoiceService service = CreateService();

        AssignAccountantResult result = await service.AssignAccountantAsync(session.Key, Guid.NewGuid(), "accountant-1", "user-payee");

        result.Should().Be(AssignAccountantResult.NotFound);
    }

    [Fact]
    public async Task ApproveInvoice_WhenApprovalServiceSucceeds_ShouldReturnSuccess()
    {
        var session = new Session(SessionType.User, "accountant-account");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("accountant-account", "user-accountant");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("user-accountant")).ReturnsAsync(7);
        _invoiceApprovalClient
            .Setup(c => c.ApproveInvoiceAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InvoiceApprovalCallResult.Success);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.ApproveInvoiceAsync(session.Key, invoice.Id, "user-accountant");

        result.Should().Be(AccountantActionResult.Success);
    }

    [Fact]
    public async Task ApproveInvoice_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorizedWithoutCallingApprovalService()
    {
        var session = new Session(SessionType.User, "accountant-account");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("accountant-account", "someone-else");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("user-accountant")).ReturnsAsync(7);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.ApproveInvoiceAsync(session.Key, invoice.Id, "user-accountant");

        result.Should().Be(AccountantActionResult.Unauthorized);
        _invoiceApprovalClient.Verify(
            c => c.ApproveInvoiceAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApproveInvoice_WithUnknownInvoice_ShouldReturnNotFound()
    {
        var session = new Session(SessionType.User, "accountant-account");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("accountant-account", "user-accountant");
        _invoiceRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Invoice?)null);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.ApproveInvoiceAsync(session.Key, Guid.NewGuid(), "user-accountant");

        result.Should().Be(AccountantActionResult.NotFound);
    }

    [Fact]
    public async Task ApproveInvoice_WithNoSession_ShouldReturnUnauthorized()
    {
        _sessionRepository.Setup(r => r.GetByKeyAsync(It.IsAny<Guid>())).ReturnsAsync((Session?)null);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.ApproveInvoiceAsync(Guid.NewGuid(), Guid.NewGuid(), "user-accountant");

        result.Should().Be(AccountantActionResult.Unauthorized);
    }

    [Fact]
    public async Task ApproveInvoice_WhenUserHasNoExternalId_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "accountant-account");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("accountant-account", "user-accountant");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("user-accountant")).ReturnsAsync((long?)null);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.ApproveInvoiceAsync(session.Key, invoice.Id, "user-accountant");

        result.Should().Be(AccountantActionResult.Unauthorized);
    }

    [Theory]
    [InlineData(InvoiceApprovalCallResult.Success, AccountantActionResult.Success)]
    [InlineData(InvoiceApprovalCallResult.NotFound, AccountantActionResult.NotFound)]
    [InlineData(InvoiceApprovalCallResult.InvalidState, AccountantActionResult.InvalidState)]
    [InlineData(InvoiceApprovalCallResult.ServiceUnavailable, AccountantActionResult.ServiceUnavailable)]
    public async Task DeclineInvoice_ShouldMapApprovalClientResults(InvoiceApprovalCallResult clientResult, AccountantActionResult expected)
    {
        var session = new Session(SessionType.User, "accountant-account");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("accountant-account", "user-accountant");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _userRepository.Setup(r => r.GetExternalIdAsync("user-accountant")).ReturnsAsync(7);
        _invoiceApprovalClient
            .Setup(c => c.DeclineInvoiceAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientResult);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.DeclineInvoiceAsync(session.Key, invoice.Id, "user-accountant");

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DeclineInvoice_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "accountant-account");
        var invoice = new Invoice("payer-1", "payee-1", 150, externalId: 42);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("accountant-account", "someone-else");
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);

        InvoiceService service = CreateService();

        AccountantActionResult result = await service.DeclineInvoiceAsync(session.Key, invoice.Id, "user-accountant");

        result.Should().Be(AccountantActionResult.Unauthorized);
        _invoiceApprovalClient.Verify(
            c => c.DeclineInvoiceAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async IAsyncEnumerable<Invoice> ToAsyncEnumerable(IEnumerable<Invoice> invoices)
    {
        foreach (Invoice invoice in invoices)
            yield return invoice;

        await Task.CompletedTask;
    }

    private void SetupOwnedAccount(string number, string ownerUserId)
    {
        _accountRepository
            .Setup(r => r.GetByNumberAsync(number))
            .ReturnsAsync(new Account(number, "1234", ownerUserId, 0));
    }

    private void SetupLockedPayment(Invoice invoice, Account payer, Account payee)
    {
        _invoiceRepository.Setup(r => r.GetByIdForUpdateAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberForUpdateAsync(payer.Number)).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberForUpdateAsync(payee.Number)).ReturnsAsync(payee);
    }

    private InvoiceService CreateService()
    {
        return new InvoiceService(
            _invoiceRepository.Object,
            _accountRepository.Object,
            _sessionRepository.Object,
            _operationRepository.Object,
            _userRepository.Object,
            _invoiceEventPublisher.Object,
            _invoiceApprovalClient.Object,
            TransactionProviderMock.Create(),
            Mock.Of<ILogger<InvoiceService>>());
    }
}
