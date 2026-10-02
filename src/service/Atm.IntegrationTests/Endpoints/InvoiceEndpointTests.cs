using Atm.Domain;
using Atm.Grpc;
using Atm.Infrastructure;
using FluentAssertions;
using Grpc.Core;
using Itmo.Dev.Platform.Common.Serialization;
using Microsoft.Extensions.DependencyInjection;
using AccountType = Atm.Domain.AccountType;
using Invoice = Atm.Domain.Invoice;
using InvoiceApprovalCallResult = Atm.Application.InvoiceApprovalCallResult;
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
    public async Task CreateInvoice_ShouldWriteExactlyOneEventToOutbox()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);
        long before = await Grpc.CountOutboxMessagesAsync();

        await Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = payer,
            Amount = "75",
            UserId = payee,
        });

        long after = await Grpc.CountOutboxMessagesAsync();
        (after - before).Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_WhenRejected_ShouldNotWriteAnythingToOutbox()
    {
        string payee = UniqueId("payee");
        await SeedUserAsync(payee);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);
        long before = await Grpc.CountOutboxMessagesAsync();

        Func<Task> act = () => Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = "does-not-exist",
            Amount = "75",
            UserId = payee,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>();

        long after = await Grpc.CountOutboxMessagesAsync();
        after.Should().Be(before);
    }

    [Fact]
    public async Task PayInvoice_WhenSentConcurrently_ShouldChargePayerOnlyOnce()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 1000);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 100);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode?[] outcomes = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => TryPayAsync(payerSession, invoice.Id, payer)));

        outcomes.Count(o => o == null).Should().Be(1);
        outcomes.Where(o => o != null).Should().OnlyContain(o => o == StatusCode.FailedPrecondition);

        (await GetBalanceAsync(payer)).Should().Be(900);
        (await GetBalanceAsync(payee)).Should().Be(100);
        (await CountOperationsAsync(payer)).Should().Be(1);
        (await CountOperationsAsync(payee)).Should().Be(1);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task PayInvoice_WhenDifferentInvoicesOfTheSamePayerArePaidConcurrently_ShouldNotLoseUpdates()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 1000);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var repository = new PostgresInvoiceRepository(Grpc.ConnectionProvider);
        Invoice[] invoices = Enumerable.Range(1, 5).Select(i => new Invoice(payer, payee, i * 10)).ToArray();

        foreach (Invoice invoice in invoices)
            await repository.SaveAsync(invoice);

        StatusCode?[] outcomes = await Task.WhenAll(invoices.Select(i => TryPayAsync(payerSession, i.Id, payer)));

        outcomes.Should().OnlyContain(o => o == null);
        (await GetBalanceAsync(payer)).Should().Be(1000 - 150);
        (await GetBalanceAsync(payee)).Should().Be(150);
    }

    [Fact]
    public async Task PayInvoice_WhenTwoAccountsPayEachOtherConcurrently_ShouldNotDeadlock()
    {
        string first = UniqueId("alpha");
        string second = UniqueId("beta");
        await SeedUserAsync(first);
        await SeedUserAsync(second);
        await SeedAccountAsync(first, first, 1000);
        await SeedAccountAsync(second, second, 1000);
        Guid firstSession = await SeedUserSessionAsync(first);
        Guid secondSession = await SeedUserSessionAsync(second);

        var repository = new PostgresInvoiceRepository(Grpc.ConnectionProvider);
        var tasks = new List<Task<StatusCode?>>();

        for (int i = 0; i < 4; i++)
        {
            var firstPays = new Invoice(first, second, 100);
            var secondPays = new Invoice(second, first, 300);
            await repository.SaveAsync(firstPays);
            await repository.SaveAsync(secondPays);

            tasks.Add(TryPayAsync(firstSession, firstPays.Id, first));
            tasks.Add(TryPayAsync(secondSession, secondPays.Id, second));
        }

        StatusCode?[] outcomes = await Task.WhenAll(tasks);

        outcomes.Should().OnlyContain(o => o == null);
        (await GetBalanceAsync(first)).Should().Be(1000 - 400 + 1200);
        (await GetBalanceAsync(second)).Should().Be(1000 + 400 - 1200);
    }

    [Fact]
    public async Task PayInvoice_WhenCancelledConcurrently_ShouldEndInExactlyOneFinalState()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 1000);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 100);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Task<StatusCode?> pay = TryPayAsync(payerSession, invoice.Id, payer);
        Task<StatusCode?> cancel = TryCancelAsync(payeeSession, invoice.Id, payee);
        StatusCode?[] outcomes = await Task.WhenAll(pay, cancel);

        outcomes.Count(o => o == null).Should().Be(1);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);

        if (loaded.Status == InvoiceStatus.Paid)
            (await GetBalanceAsync(payer)).Should().Be(900);
        else
            (await GetBalanceAsync(payer)).Should().Be(1000);
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

        Func<Task> act = () => Grpc.InvoiceClient.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            SessionKey = payeeSession.ToString(),
            PayerAccountNumber = payer,
            Amount = "75",
            UserId = attacker,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
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

        StatusCode? outcome = await TryPayAsync(payerSession, invoice.Id, attacker);

        outcome.Should().Be(StatusCode.PermissionDenied);
        (await GetBalanceAsync(payer)).Should().Be(200);
        (await GetBalanceAsync(payee)).Should().Be(0);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Created);
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

        StatusCode? outcome = await TryCancelAsync(payeeSession, invoice.Id, attacker);

        outcome.Should().Be(StatusCode.PermissionDenied);

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task CancelInvoice_ByIssuerOwner_ShouldMarkInvoiceCancelled()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryCancelAsync(payeeSession, invoice.Id, payee);

        outcome.Should().BeNull();

        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(invoice.Id);
        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Cancelled);
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

        Func<Task> act = () => Grpc.InvoiceClient.GetOutgoingInvoicesAsync(new GetOutgoingInvoicesRequest
        {
            SessionKey = payeeSession.ToString(),
            PageSize = 10,
            UserId = attacker,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
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

        Func<Task> act = () => Grpc.InvoiceClient.GetIncomingInvoicesAsync(new GetIncomingInvoicesRequest
        {
            SessionKey = payerSession.ToString(),
            PageSize = 10,
            UserId = attacker,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task GetOutgoingInvoices_ForAccountOwner_ShouldReturnItsInvoices()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        GetInvoicesResponse response = await Grpc.InvoiceClient.GetOutgoingInvoicesAsync(new GetOutgoingInvoicesRequest
        {
            SessionKey = payeeSession.ToString(),
            PageSize = 10,
            UserId = payee,
        });

        response.Invoices.Should().ContainSingle(i => i.Id == invoice.Id.ToString());
    }

    [Fact]
    public async Task AssignAccountant_ShouldForwardExternalIdsToApprovalService()
    {
        Grpc.ApprovalClient.Reset();

        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string accountant = UniqueId("accountant");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedUserAsync(accountant);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        long invoiceExternalId = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);
        long accountantExternalId = (await new PostgresUserRepository(Grpc.ConnectionProvider).GetExternalIdAsync(accountant)).GetValueOrDefault();

        await Grpc.InvoiceClient.AssignAccountantAsync(new AssignAccountantRequest
        {
            SessionKey = payeeSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = payee,
            AccountantUserId = accountant,
        });

        Grpc.ApprovalClient.Calls.Should().ContainSingle()
            .Which.Should().Be(new FakeInvoiceApprovalClient.ApprovalCall("assign", invoiceExternalId, accountantExternalId));
    }

    [Fact]
    public async Task AssignAccountant_WithUnknownAccountant_ShouldFailWithNotFound()
    {
        Grpc.ApprovalClient.Reset();

        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Func<Task> act = () => Grpc.InvoiceClient.AssignAccountantAsync(new AssignAccountantRequest
        {
            SessionKey = payeeSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = payee,
            AccountantUserId = UniqueId("ghost"),
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.NotFound);
        Grpc.ApprovalClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AssignAccountant_WhenCallerIsNotTheIssuer_ShouldFailWithPermissionDenied()
    {
        Grpc.ApprovalClient.Reset();

        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string accountant = UniqueId("accountant");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedUserAsync(accountant);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Func<Task> act = () => Grpc.InvoiceClient.AssignAccountantAsync(new AssignAccountantRequest
        {
            SessionKey = payerSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = payer,
            AccountantUserId = accountant,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
        Grpc.ApprovalClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AssignAccountant_WhenJwtUserDoesNotOwnIssuerAccount_ShouldFailWithPermissionDenied()
    {
        Grpc.ApprovalClient.Reset();

        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string owner = UniqueId("owner");
        string attacker = UniqueId("attacker");
        string accountant = UniqueId("accountant");
        await SeedUserAsync(payer);
        await SeedUserAsync(owner);
        await SeedUserAsync(attacker);
        await SeedUserAsync(accountant);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, owner, 0);
        Guid payeeSession = await SeedUserSessionAsync(payee);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        Func<Task> act = () => Grpc.InvoiceClient.AssignAccountantAsync(new AssignAccountantRequest
        {
            SessionKey = payeeSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = attacker,
            AccountantUserId = accountant,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
        Grpc.ApprovalClient.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(InvoiceApprovalCallResult.Success, null)]
    [InlineData(InvoiceApprovalCallResult.NotFound, StatusCode.NotFound)]
    [InlineData(InvoiceApprovalCallResult.InvalidState, StatusCode.FailedPrecondition)]
    [InlineData(InvoiceApprovalCallResult.ServiceUnavailable, StatusCode.Unavailable)]
    public async Task ApproveInvoice_ShouldForwardCallerExternalIdAndMapResult(InvoiceApprovalCallResult approvalResult, StatusCode? expectedFailure)
    {
        Grpc.ApprovalClient.Reset();
        Grpc.ApprovalClient.Result = approvalResult;

        try
        {
            (Guid accountantSession, string accountant, Invoice invoice, long invoiceExternalId) = await SeedAccountantScenarioAsync();
            long accountantExternalId = (await new PostgresUserRepository(Grpc.ConnectionProvider).GetExternalIdAsync(accountant)).GetValueOrDefault();

            Func<Task> act = () => Grpc.InvoiceClient.ApproveInvoiceAsync(new InvoiceRequest
            {
                SessionKey = accountantSession.ToString(),
                InvoiceId = invoice.Id.ToString(),
                UserId = accountant,
            }).ResponseAsync;

            if (expectedFailure == null)
                await act.Should().NotThrowAsync();
            else
                await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == expectedFailure);

            Grpc.ApprovalClient.Calls.Should().ContainSingle()
                .Which.Should().Be(new FakeInvoiceApprovalClient.ApprovalCall("approve", invoiceExternalId, accountantExternalId));
        }
        finally
        {
            Grpc.ApprovalClient.Reset();
        }
    }

    [Theory]
    [InlineData(InvoiceApprovalCallResult.Success, null)]
    [InlineData(InvoiceApprovalCallResult.NotFound, StatusCode.NotFound)]
    [InlineData(InvoiceApprovalCallResult.InvalidState, StatusCode.FailedPrecondition)]
    [InlineData(InvoiceApprovalCallResult.ServiceUnavailable, StatusCode.Unavailable)]
    public async Task DeclineInvoice_ShouldForwardCallerExternalIdAndMapResult(InvoiceApprovalCallResult approvalResult, StatusCode? expectedFailure)
    {
        Grpc.ApprovalClient.Reset();
        Grpc.ApprovalClient.Result = approvalResult;

        try
        {
            (Guid accountantSession, string accountant, Invoice invoice, long invoiceExternalId) = await SeedAccountantScenarioAsync();
            long accountantExternalId = (await new PostgresUserRepository(Grpc.ConnectionProvider).GetExternalIdAsync(accountant)).GetValueOrDefault();

            Func<Task> act = () => Grpc.InvoiceClient.DeclineInvoiceAsync(new InvoiceRequest
            {
                SessionKey = accountantSession.ToString(),
                InvoiceId = invoice.Id.ToString(),
                UserId = accountant,
            }).ResponseAsync;

            if (expectedFailure == null)
                await act.Should().NotThrowAsync();
            else
                await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == expectedFailure);

            Grpc.ApprovalClient.Calls.Should().ContainSingle()
                .Which.Should().Be(new FakeInvoiceApprovalClient.ApprovalCall("decline", invoiceExternalId, accountantExternalId));
        }
        finally
        {
            Grpc.ApprovalClient.Reset();
        }
    }

    [Fact]
    public async Task ApproveInvoice_WhenJwtUserDoesNotOwnSessionAccount_ShouldFailWithPermissionDenied()
    {
        Grpc.ApprovalClient.Reset();

        (Guid accountantSession, _, Invoice invoice, _) = await SeedAccountantScenarioAsync();
        string attacker = UniqueId("attacker");
        await SeedUserAsync(attacker);

        Func<Task> act = () => Grpc.InvoiceClient.ApproveInvoiceAsync(new InvoiceRequest
        {
            SessionKey = accountantSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = attacker,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
        Grpc.ApprovalClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DeclineInvoice_WhenJwtUserDoesNotOwnSessionAccount_ShouldFailWithPermissionDenied()
    {
        Grpc.ApprovalClient.Reset();

        (Guid accountantSession, _, Invoice invoice, _) = await SeedAccountantScenarioAsync();
        string attacker = UniqueId("attacker");
        await SeedUserAsync(attacker);

        Func<Task> act = () => Grpc.InvoiceClient.DeclineInvoiceAsync(new InvoiceRequest
        {
            SessionKey = accountantSession.ToString(),
            InvoiceId = invoice.Id.ToString(),
            UserId = attacker,
        }).ResponseAsync;

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
        Grpc.ApprovalClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PayInvoice_ForCorporatePayerBeforeApproval_ShouldFailWithFailedPrecondition()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(payer, "1234", payer, 500, AccountType.Corporate));
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryPayAsync(payerSession, invoice.Id, payer);

        outcome.Should().Be(StatusCode.FailedPrecondition);
        (await GetBalanceAsync(payer)).Should().Be(500);
    }

    [Fact]
    public async Task PayInvoice_ForCorporatePayerAfterApproval_ShouldMoveMoney()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(payer, "1234", payer, 500, AccountType.Corporate));
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        invoice.Approve();
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryPayAsync(payerSession, invoice.Id, payer);

        outcome.Should().BeNull();
        (await GetBalanceAsync(payer)).Should().Be(450);
        (await GetBalanceAsync(payee)).Should().Be(50);
    }

    [Fact]
    public async Task PayInvoice_WhenDeclined_ShouldFailWithFailedPrecondition()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await new PostgresAccountRepository(Grpc.ConnectionProvider).SaveAsync(new Account(payer, "1234", payer, 500, AccountType.Corporate));
        await SeedAccountAsync(payee, payee, 0);
        Guid payerSession = await SeedUserSessionAsync(payer);

        var invoice = new Invoice(payer, payee, 50);
        invoice.Decline();
        await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        StatusCode? outcome = await TryPayAsync(payerSession, invoice.Id, payer);

        outcome.Should().Be(StatusCode.FailedPrecondition);
        (await GetBalanceAsync(payer)).Should().Be(500);
    }

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<(Guid AccountantSession, string Accountant, Invoice Invoice, long InvoiceExternalId)> SeedAccountantScenarioAsync()
    {
        string payer = UniqueId("payer");
        string payee = UniqueId("payee");
        string accountant = UniqueId("accountant");
        string accountantAccount = UniqueId("acct");
        await SeedUserAsync(payer);
        await SeedUserAsync(payee);
        await SeedUserAsync(accountant);
        await SeedAccountAsync(payer, payer, 0);
        await SeedAccountAsync(payee, payee, 0);
        await SeedAccountAsync(accountantAccount, accountant, 0);
        Guid accountantSession = await SeedUserSessionAsync(accountantAccount);

        var invoice = new Invoice(payer, payee, 50);
        long invoiceExternalId = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        return (accountantSession, accountant, invoice, invoiceExternalId);
    }

    private async Task<StatusCode?> TryPayAsync(Guid sessionKey, Guid invoiceId, string userId)
    {
        try
        {
            await Grpc.InvoiceClient.PayInvoiceAsync(new InvoiceRequest
            {
                SessionKey = sessionKey.ToString(),
                InvoiceId = invoiceId.ToString(),
                UserId = userId,
            });

            return null;
        }
        catch (RpcException exception)
        {
            return exception.StatusCode;
        }
    }

    private async Task<StatusCode?> TryCancelAsync(Guid sessionKey, Guid invoiceId, string userId)
    {
        try
        {
            await Grpc.InvoiceClient.CancelInvoiceAsync(new InvoiceRequest
            {
                SessionKey = sessionKey.ToString(),
                InvoiceId = invoiceId.ToString(),
                UserId = userId,
            });

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

    private async Task<int> CountOperationsAsync(string accountNumber)
    {
        var repository = new PostgresOperationRepository(Grpc.ConnectionProvider, Grpc.Services.GetRequiredService<IPlatformSerializer>());
        int count = 0;

        await foreach (Operation unused in repository.GetByAccountNumberAsync(accountNumber, null, 100))
            count++;

        return count;
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
