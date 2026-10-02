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

    [Fact]
    public async Task CreateInvoice_WithExistingPayer_ShouldSucceed()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.ExistsAsync("payer-1")).ReturnsAsync(true);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "payer-1", 150, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.Success);
        result.InvoiceId.Should().NotBeNull();
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Once);
    }

    [Fact]
    public async Task CreateInvoice_WithUnknownPayer_ShouldReturnPayerNotFound()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.ExistsAsync("payer-1")).ReturnsAsync(false);

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
        _accountRepository.Setup(r => r.ExistsAsync("payer-1")).ReturnsAsync(true);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "payer-1", 150, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.Unauthorized);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("-1000")]
    public async Task CreateInvoice_WithZeroOrNegativeAmount_ShouldReturnInvalidAmountAndNotSave(string amountText)
    {
        decimal amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.ExistsAsync("payer-1")).ReturnsAsync(true);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "payer-1", amount, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.InvalidAmount);
        result.InvoiceId.Should().BeNull();
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Fact]
    public async Task CreateInvoice_WithSmallestPositiveAmount_ShouldSucceed()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        SetupOwnedAccount("payee-1", "user-payee");
        _accountRepository.Setup(r => r.ExistsAsync("payer-1")).ReturnsAsync(true);

        InvoiceService service = CreateService();

        CreateInvoiceResult result = await service.CreateInvoiceAsync(session.Key, "payer-1", 0.01m, "user-payee");

        result.Status.Should().Be(CreateInvoiceStatus.Success);
    }

    [Fact]
    public async Task PayInvoice_WithSufficientBalance_ShouldUpdateBothAccountsAndRecordTwoOperations()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);

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
    public async Task PayInvoice_WhenNotThePayer_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "someone-else");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.Unauthorized);
        invoice.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task PayInvoice_WhenPayerAccountBelongsToAnotherUser_ShouldReturnUnauthorizedAndKeepEverythingUntouched()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "intruder-user");

        result.Should().Be(PayInvoiceResult.Unauthorized);
        invoice.Status.Should().Be(InvoiceStatus.Created);
        payer.Balance.Should().Be(1000);
        payee.Balance.Should().Be(0);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
        _operationRepository.Verify(r => r.SaveAsync(It.IsAny<Operation>()), Times.Never);
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
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.AlreadyProcessed);
        payer.Balance.Should().Be(1000);
        _operationRepository.Verify(r => r.SaveAsync(It.IsAny<Operation>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoice_WhenCancelled_ShouldReturnAlreadyProcessedAndNotMoveMoney()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 1000);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Cancel();

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);

        InvoiceService service = CreateService();

        PayInvoiceResult result = await service.PayInvoiceAsync(session.Key, invoice.Id, "user-payer");

        result.Should().Be(PayInvoiceResult.AlreadyProcessed);
        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
        payer.Balance.Should().Be(1000);
        payee.Balance.Should().Be(0);
        _accountRepository.Verify(r => r.SaveAsync(It.IsAny<Account>()), Times.Never);
        _operationRepository.Verify(r => r.SaveAsync(It.IsAny<Operation>()), Times.Never);
    }

    [Fact]
    public async Task PayInvoice_WithInsufficientFunds_ShouldReturnInsufficientFundsAndKeepInvoiceCreated()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 50);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);

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
    public async Task PayInvoice_AfterInsufficientFundsAndTopUp_ShouldSucceed()
    {
        var payer = new Account("payer-1", "1111", "user-payer", 50);
        var payee = new Account("payee-1", "2222", "user-payee", 0);
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        _accountRepository.Setup(r => r.GetByNumberAsync("payer-1")).ReturnsAsync(payer);
        _accountRepository.Setup(r => r.GetByNumberAsync("payee-1")).ReturnsAsync(payee);

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
        _invoiceRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Invoice?)null);

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

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.Success);
        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
        _invoiceRepository.Verify(r => r.SaveAsync(invoice), Times.Once);
    }

    [Fact]
    public async Task CancelInvoice_ByPayer_ShouldReturnUnauthorized()
    {
        var session = new Session(SessionType.User, "payer-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);

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
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "someone-else");

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

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.AlreadyProcessed);
    }

    [Fact]
    public async Task CancelInvoice_WhenAlreadyPaid_ShouldReturnAlreadyProcessedAndKeepInvoicePaid()
    {
        var session = new Session(SessionType.User, "payee-1");
        var invoice = new Invoice("payer-1", "payee-1", 150);
        invoice.Pay();

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(invoice.Id)).ReturnsAsync(invoice);
        SetupOwnedAccount("payee-1", "user-payee");

        InvoiceService service = CreateService();

        CancelInvoiceResult result = await service.CancelInvoiceAsync(session.Key, invoice.Id, "user-payee");

        result.Should().Be(CancelInvoiceResult.AlreadyProcessed);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        _invoiceRepository.Verify(r => r.SaveAsync(It.IsAny<Invoice>()), Times.Never);
    }

    [Fact]
    public async Task CancelInvoice_WithUnknownInvoiceId_ShouldReturnNotFound()
    {
        var session = new Session(SessionType.User, "payee-1");

        _sessionRepository.Setup(r => r.GetByKeyAsync(session.Key)).ReturnsAsync(session);
        _invoiceRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Invoice?)null);

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
    public async Task GetOutgoingInvoices_WhenSessionAccountBelongsToAnotherUser_ShouldReturnUnauthorizedWithoutReadingInvoices()
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

    private InvoiceService CreateService()
    {
        return new InvoiceService(
            _invoiceRepository.Object,
            _accountRepository.Object,
            _sessionRepository.Object,
            _operationRepository.Object,
            TransactionProviderMock.Create(),
            Mock.Of<ILogger<InvoiceService>>());
    }
}
