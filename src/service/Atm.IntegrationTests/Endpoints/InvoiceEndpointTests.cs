using Atm.Domain;
using Atm.Grpc;
using Atm.Infrastructure;
using FluentAssertions;
using Grpc.Core;
using Invoice = Atm.Domain.Invoice;
using InvoiceStatus = Atm.Domain.InvoiceStatus;

namespace Atm.IntegrationTests.Endpoints;

public class InvoiceEndpointTests : GrpcServiceTestBase
{
    public InvoiceEndpointTests(AtmGrpcServiceFixture grpc)
        : base(grpc)
    {
    }

    [Fact]
    public async Task CreateInvoice_ForExistingPayer_ShouldSucceed()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        CreateInvoiceResponse response = await Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = payer,
            Amount = "75",
            UserId = payee,
        });

        response.InvoiceId.Should().NotBeNullOrEmpty();

        Invoice? invoice = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(Guid.Parse(response.InvoiceId));
        Assert.NotNull(invoice);
        invoice.PayerAccountNumber.Should().Be(payer);
        invoice.PayeeAccountNumber.Should().Be(payee);
        invoice.Amount.Should().Be(75);
        invoice.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task CreateInvoice_WithUnknownPayer_ShouldFailWithNotFound()
    {
        string payee = UniqueId("payee");
        await SeedUserAsync(payee);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        Func<Task> act = () => Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = "does-not-exist",
            Amount = "75",
            UserId = payee,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task PayInvoice_WithSufficientFunds_ShouldMoveMoneyAndMarkPaid()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 200);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        await Grpc.InvoiceClient.PayInvoiceAsync(new InvoiceRequest
        {
            SessionKey = payerSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = payer,
        });

        Invoice? loadedInvoice = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loadedInvoice);
        loadedInvoice.Status.Should().Be(InvoiceStatus.Paid);

        Account? payerAccount = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(payer);
        Account? payeeAccount = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(payee);
        Assert.NotNull(payerAccount);
        Assert.NotNull(payeeAccount);
        payerAccount.Balance.Should().Be(150);
        payeeAccount.Balance.Should().Be(50);
    }

    [Fact]
    public async Task PayInvoice_WithInsufficientFunds_ShouldFailWithFailedPreconditionAndLeaveInvoiceUnpaid()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 10);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Func<Task> act = () => Grpc.InvoiceClient.PayInvoiceAsync(new InvoiceRequest
        {
            SessionKey = payerSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = payer,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.FailedPrecondition);

        Invoice? loadedInvoice = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loadedInvoice);
        loadedInvoice.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task PayInvoice_WhenCallerIsNotThePayer_ShouldFailWithPermissionDenied()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string intruder = UniqueId("intruder");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedUserAsync(intruder);
        await SeedAccountAsync(payer, payer, 200);
        await SeedAccountAsync(payee, payee, 0);
        await SeedAccountAsync(intruder, intruder, 200);
        Guid intruderSession = await SeedUserSessionAsync(intruder);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Func<Task> act = () => Grpc.InvoiceClient.PayInvoiceAsync(new InvoiceRequest
        {
            SessionKey = intruderSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = intruder,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task PayInvoice_WhenAlreadyPaid_ShouldFailWithFailedPrecondition()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 200);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        invoice.Pay();
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Func<Task> act = () => Grpc.InvoiceClient.PayInvoiceAsync(new InvoiceRequest
        {
            SessionKey = payerSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = payer,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task PayInvoice_WhenCancelled_ShouldFailWithFailedPreconditionAndKeepMoney()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 200);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        invoice.Cancel();
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.PayInvoiceAsync(PayRequest(payerSession, invoice.Id, payer)).ResponseAsync);

        outcome.Should().Be(StatusCode.FailedPrecondition);
        (await GetBalanceAsync(payer)).Should().Be(200);
        (await GetBalanceAsync(payee)).Should().Be(0);
    }

    [Fact]
    public async Task CancelInvoice_WhenAlreadyPaid_ShouldFailWithFailedPreconditionAndKeepInvoicePaid()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 200);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        invoice.Pay();
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.CancelInvoiceAsync(PayRequest(payeeSession, invoice.Id, payee)).ResponseAsync);

        outcome.Should().Be(StatusCode.FailedPrecondition);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task PayInvoice_WhenJwtUserDoesNotOwnPayerAccount_ShouldFailWithPermissionDeniedAndKeepMoney()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string owner = UniqueId("owner");
        string attacker = UniqueId("attacker");
        await SeedUserAsync(payee);
        await SeedUserAsync(owner);
        await SeedUserAsync(attacker);
        await SeedAccountAsync(payer, owner, 200);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.PayInvoiceAsync(PayRequest(payerSession, invoice.Id, attacker)).ResponseAsync);

        outcome.Should().Be(StatusCode.PermissionDenied);
        (await GetBalanceAsync(payer)).Should().Be(200);
        (await GetBalanceAsync(payee)).Should().Be(0);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task CreateInvoice_WhenJwtUserDoesNotOwnIssuerAccount_ShouldFailWithPermissionDenied()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string owner = UniqueId("owner");
        string attacker = UniqueId("attacker");
        await SeedUserAsync(payer);
        await SeedUserAsync(owner);
        await SeedUserAsync(attacker);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, owner, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = payer,
            Amount = "75",
            UserId = attacker,
        }).ResponseAsync);

        outcome.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task CancelInvoice_WhenJwtUserDoesNotOwnIssuerAccount_ShouldFailWithPermissionDenied()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string owner = UniqueId("owner");
        string attacker = UniqueId("attacker");
        await SeedUserAsync(payer);
        await SeedUserAsync(owner);
        await SeedUserAsync(attacker);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, owner, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.CancelInvoiceAsync(PayRequest(payeeSession, invoice.Id, attacker)).ResponseAsync);

        outcome.Should().Be(StatusCode.PermissionDenied);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task GetOutgoingInvoices_WhenJwtUserDoesNotOwnSessionAccount_ShouldFailWithPermissionDenied()
    {
        string payee = UniqueId("payee");
        string owner = UniqueId("owner");
        string attacker = UniqueId("attacker");
        await SeedUserAsync(owner);
        await SeedUserAsync(attacker);
        await SeedAccountAsync(payee, owner, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.GetOutgoingInvoicesAsync(new GetOutgoingInvoicesRequest
        {
            SessionKey = payeeSession.ToString(),
            PageSize = 10,
            UserId = attacker,
        }).ResponseAsync);

        outcome.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task GetIncomingInvoices_WhenJwtUserDoesNotOwnSessionAccount_ShouldFailWithPermissionDenied()
    {
        string payer = UniqueId("payer");
        string owner = UniqueId("owner");
        string attacker = UniqueId("attacker");
        await SeedUserAsync(owner);
        await SeedUserAsync(attacker);
        await SeedAccountAsync(payer, owner, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.GetIncomingInvoicesAsync(new GetIncomingInvoicesRequest
        {
            SessionKey = payerSession.ToString(),
            PageSize = 10,
            UserId = attacker,
        }).ResponseAsync);

        outcome.Should().Be(StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    public async Task CreateInvoice_WithZeroOrNegativeAmount_ShouldFailWithInvalidArgumentAndSaveNothing(string amount)
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        StatusCode? outcome = await TryAsync(() => Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = payer,
            Amount = amount,
            UserId = payee,
        }).ResponseAsync);

        outcome.Should().Be(StatusCode.InvalidArgument);

        GetInvoicesResponse outgoing = await Grpc.InvoiceClient.GetOutgoingInvoicesAsync(new GetOutgoingInvoicesRequest
        {
            SessionKey = payeeSession.ToString(),
            PageSize = 10,
            UserId = payee,
        });

        outgoing.Invoices.Should().BeEmpty();
    }

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static InvoiceRequest PayRequest(Guid sessionKey, Guid invoiceId, string userId)
    {
        return new InvoiceRequest
        {
            SessionKey = sessionKey.ToString(),
            InvoiceId = invoiceId.ToString(),
            UserId = userId,
        };
    }

    private static async Task<StatusCode?> TryAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (RpcException exception)
        {
            return exception.StatusCode;
        }
    }

    private async Task<decimal> GetBalanceAsync(string accountNumber)
    {
        Account? account = await new PostgresAccountRepository(Grpc.ConnectionProvider).GetByNumberAsync(accountNumber);
        Assert.NotNull(account);
        return account.Balance;
    }

    private async Task<Guid> SeedUserSessionAsync(string accountNumber)
    {
        var session = new Session(SessionType.User, accountNumber);
        await new PostgresSessionRepository(Grpc.ConnectionProvider).SaveAsync(session);
        return session.Key;
    }

    private async Task SeedUserAsync(string id)
    {
        await new PostgresUserRepository(Grpc.ConnectionProvider).SaveAsync(new User(id, DateTime.UtcNow));
    }

    private async Task SeedAccountAsync(string number, string ownerUserId, decimal balance)
    {
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(number, "1234", ownerUserId, balance));
    }
}
