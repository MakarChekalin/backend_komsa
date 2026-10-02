using Atm.Domain;
using Atm.Infrastructure;
using FluentAssertions;

namespace Atm.IntegrationTests.Repositories;

public class PostgresInvoiceRepositoryTests : RepositoryTestBase
{
    public PostgresInvoiceRepositoryTests(AtmDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_ShouldReturnInvoice()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 123.45m);

        await repository.SaveAsync(invoice);

        Invoice? loaded = await repository.GetByIdAsync(invoice.Id);

        Assert.NotNull(loaded);
        loaded.Id.Should().Be(invoice.Id);
        loaded.PayerAccountNumber.Should().Be("payer-1");
        loaded.PayeeAccountNumber.Should().Be("payee-1");
        loaded.Amount.Should().Be(123.45m);
        loaded.Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task GetByIdAsync_WithUnknownId_ShouldReturnNull()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        Invoice? loaded = await repository.GetByIdAsync(Guid.NewGuid());

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_WhenInvoiceExists_ShouldUpdateStatusOnly()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 100);
        await repository.SaveAsync(invoice);

        invoice.Pay();
        await repository.SaveAsync(invoice);

        Invoice? loaded = await repository.GetByIdAsync(invoice.Id);

        Assert.NotNull(loaded);
        loaded.Status.Should().Be(InvoiceStatus.Paid);
        loaded.Amount.Should().Be(100);
    }

    [Fact]
    public async Task GetOutgoingAsync_ShouldReturnInvoicesIssuedByAccount()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new Invoice("payer-1", "payee-1", 10));
        await repository.SaveAsync(new Invoice("payer-2", "payee-1", 20));
        await repository.SaveAsync(new Invoice("payer-1", "other-payee", 30));

        List<Invoice> result = await CollectAsync(repository.GetOutgoingAsync("payee-1", null, null, null, 10));

        result.Should().HaveCount(2);
        result.Should().OnlyContain(i => i.PayeeAccountNumber == "payee-1");
    }

    [Fact]
    public async Task GetOutgoingAsync_FilteredByPayerAndStatus_ShouldReturnOnlyMatching()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        var matching = new Invoice("payer-1", "payee-1", 10);
        var wrongPayer = new Invoice("payer-2", "payee-1", 20);
        var wrongStatus = new Invoice("payer-1", "payee-1", 30);
        wrongStatus.Cancel();

        await repository.SaveAsync(matching);
        await repository.SaveAsync(wrongPayer);
        await repository.SaveAsync(wrongStatus);

        List<Invoice> result = await CollectAsync(
            repository.GetOutgoingAsync("payee-1", "payer-1", InvoiceStatus.Created, null, 10));

        result.Should().ContainSingle();
        result[0].Id.Should().Be(matching.Id);
    }

    [Fact]
    public async Task GetOutgoingAsync_WithPagination_ShouldRespectCursorAndOrder()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        var first = new Invoice("payer-1", "payee-1", 10, createdAt: DateTime.UtcNow.AddMinutes(-3));
        var second = new Invoice("payer-1", "payee-1", 20, createdAt: DateTime.UtcNow.AddMinutes(-2));
        var third = new Invoice("payer-1", "payee-1", 30, createdAt: DateTime.UtcNow.AddMinutes(-1));

        await repository.SaveAsync(first);
        await repository.SaveAsync(second);
        await repository.SaveAsync(third);

        List<Invoice> firstPage = await CollectAsync(repository.GetOutgoingAsync("payee-1", null, null, null, 2));
        firstPage.Select(i => i.Id).Should().Equal(first.Id, second.Id);

        List<Invoice> secondPage = await CollectAsync(repository.GetOutgoingAsync("payee-1", null, null, firstPage[^1].Id, 2));
        secondPage.Select(i => i.Id).Should().Equal(third.Id);
    }

    [Fact]
    public async Task GetIncomingAsync_ShouldReturnInvoicesPayableByAccount()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        await repository.SaveAsync(new Invoice("payer-1", "payee-1", 10));
        await repository.SaveAsync(new Invoice("payer-1", "payee-2", 20));

        List<Invoice> result = await CollectAsync(repository.GetIncomingAsync("payer-1", null, null, null, 10));

        result.Should().HaveCount(2);
        result.Should().OnlyContain(i => i.PayerAccountNumber == "payer-1");
    }

    [Fact]
    public async Task SaveAsync_ShouldReturnExternalIdMatchingTheLoadedInvoice()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 10);

        long externalId = await repository.SaveAsync(invoice);
        Invoice? loaded = await repository.GetByIdAsync(invoice.Id);

        Assert.NotNull(loaded);
        externalId.Should().BeGreaterThan(0);
        loaded.ExternalId.Should().Be(externalId);
    }

    [Fact]
    public async Task SaveAsync_ForDifferentInvoices_ShouldReturnDifferentExternalIds()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        long first = await repository.SaveAsync(new Invoice("payer-1", "payee-1", 10));
        long second = await repository.SaveAsync(new Invoice("payer-1", "payee-1", 10));

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task SaveAsync_WhenInvoiceExists_ShouldReturnTheSameExternalId()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 10);

        long first = await repository.SaveAsync(invoice);
        invoice.Pay();
        long second = await repository.SaveAsync(invoice);

        second.Should().Be(first);
    }

    [Fact]
    public async Task GetByExternalIdAsync_ShouldReturnInvoiceWithThatExternalId()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 10);
        await repository.SaveAsync(new Invoice("payer-2", "payee-2", 99));
        long externalId = await repository.SaveAsync(invoice);

        Invoice? loaded = await repository.GetByExternalIdAsync(externalId);

        Assert.NotNull(loaded);
        loaded.Id.Should().Be(invoice.Id);
        loaded.ExternalId.Should().Be(externalId);
    }

    [Fact]
    public async Task GetByExternalIdAsync_WithUnknownExternalId_ShouldReturnNull()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        Invoice? loaded = await repository.GetByExternalIdAsync(long.MaxValue);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdForUpdateAsync_ShouldReturnTheSameInvoiceAsGetByIdAsync()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 77.5m);
        await repository.SaveAsync(invoice);

        Invoice? plain = await repository.GetByIdAsync(invoice.Id);
        Invoice? locked = await repository.GetByIdForUpdateAsync(invoice.Id);

        Assert.NotNull(plain);
        Assert.NotNull(locked);
        locked.Id.Should().Be(plain.Id);
        locked.Amount.Should().Be(77.5m);
        locked.Status.Should().Be(plain.Status);
        locked.ExternalId.Should().Be(plain.ExternalId);
    }

    [Fact]
    public async Task GetByIdForUpdateAsync_WithUnknownId_ShouldReturnNull()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        Invoice? loaded = await repository.GetByIdForUpdateAsync(Guid.NewGuid());

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetByExternalIdForUpdateAsync_ShouldReturnInvoiceWithThatExternalId()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 10);
        long externalId = await repository.SaveAsync(invoice);

        Invoice? loaded = await repository.GetByExternalIdForUpdateAsync(externalId);

        Assert.NotNull(loaded);
        loaded.Id.Should().Be(invoice.Id);
    }

    [Fact]
    public async Task GetByExternalIdForUpdateAsync_WithUnknownExternalId_ShouldReturnNull()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        Invoice? loaded = await repository.GetByExternalIdForUpdateAsync(long.MaxValue);

        loaded.Should().BeNull();
    }

    [Theory]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Declined)]
    public async Task SaveAsync_ThenGetByIdAsync_ShouldRoundTripApprovalStatuses(InvoiceStatus status)
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);
        var invoice = new Invoice("payer-1", "payee-1", 10);

        if (status == InvoiceStatus.Approved)
            invoice.Approve();
        else
            invoice.Decline();

        await repository.SaveAsync(invoice);

        Invoice? loaded = await repository.GetByIdAsync(invoice.Id);

        Assert.NotNull(loaded);
        loaded.Status.Should().Be(status);
    }

    [Fact]
    public async Task GetIncomingAsync_FilteredByApprovedStatus_ShouldReturnOnlyApprovedInvoices()
    {
        var repository = new PostgresInvoiceRepository(Fixture.ConnectionProvider);

        var approved = new Invoice("payer-1", "payee-1", 10);
        approved.Approve();
        var pending = new Invoice("payer-1", "payee-1", 20);

        await repository.SaveAsync(approved);
        await repository.SaveAsync(pending);

        List<Invoice> result = await CollectAsync(
            repository.GetIncomingAsync("payer-1", null, InvoiceStatus.Approved, null, 10));

        result.Should().ContainSingle();
        result[0].Id.Should().Be(approved.Id);
    }

    private static async Task<List<Invoice>> CollectAsync(IAsyncEnumerable<Invoice> source)
    {
        var result = new List<Invoice>();

        await foreach (Invoice invoice in source)
            result.Add(invoice);

        return result;
    }
}
