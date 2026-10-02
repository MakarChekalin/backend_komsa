using ApprovalResult.Kafka.Contracts;
using Atm.Grpc.Kafka;
using Atm.Infrastructure;
using FluentAssertions;
using Itmo.Dev.Platform.Kafka.Consumer;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Invoice = Atm.Domain.Invoice;
using InvoiceStatus = Atm.Domain.InvoiceStatus;

namespace Atm.IntegrationTests.Kafka;

public class ApprovalResultKafkaHandlerTests : GrpcServiceTestBase
{
    public ApprovalResultKafkaHandlerTests(AtmGrpcServiceFixture grpc)
        : base(grpc)
    {
    }

    [Fact]
    public async Task HandleAsync_WithApprovedResult_ShouldMoveCreatedInvoiceToApproved()
    {
        (Invoice invoice, long externalId) = await SeedInvoiceAsync();

        await HandleAsync(Message(externalId, ApprovalStatus.Approved));

        (await LoadAsync(invoice.Id)).Status.Should().Be(InvoiceStatus.Approved);
    }

    [Fact]
    public async Task HandleAsync_WithDeclinedResult_ShouldMoveCreatedInvoiceToDeclined()
    {
        (Invoice invoice, long externalId) = await SeedInvoiceAsync();

        await HandleAsync(Message(externalId, ApprovalStatus.Declined));

        (await LoadAsync(invoice.Id)).Status.Should().Be(InvoiceStatus.Declined);
    }

    [Fact]
    public async Task HandleAsync_WhenInvoiceIsAlreadyPaid_ShouldNotChangeIt()
    {
        var invoice = new Invoice("payer-1", "payee-1", 10);
        invoice.Pay();
        long externalId = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        await HandleAsync(Message(externalId, ApprovalStatus.Approved));

        (await LoadAsync(invoice.Id)).Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task HandleAsync_WhenInvoiceIsAlreadyDeclined_ShouldIgnoreLaterApproval()
    {
        (Invoice invoice, long externalId) = await SeedInvoiceAsync();

        await HandleAsync(Message(externalId, ApprovalStatus.Declined), Message(externalId, ApprovalStatus.Approved));

        (await LoadAsync(invoice.Id)).Status.Should().Be(InvoiceStatus.Declined);
    }

    [Fact]
    public async Task HandleAsync_WithUnspecifiedStatus_ShouldNotChangeInvoice()
    {
        (Invoice invoice, long externalId) = await SeedInvoiceAsync();

        await HandleAsync(Message(externalId, ApprovalStatus.Unspecified));

        (await LoadAsync(invoice.Id)).Status.Should().Be(InvoiceStatus.Created);
    }

    [Fact]
    public async Task HandleAsync_ForUnknownInvoice_ShouldNotThrow()
    {
        Func<Task> act = () => HandleAsync(Message(long.MaxValue, ApprovalStatus.Approved));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleAsync_WithSeveralMessages_ShouldApplyEachOfThem()
    {
        (Invoice first, long firstExternalId) = await SeedInvoiceAsync();
        (Invoice second, long secondExternalId) = await SeedInvoiceAsync();

        await HandleAsync(Message(firstExternalId, ApprovalStatus.Approved), Message(secondExternalId, ApprovalStatus.Declined));

        (await LoadAsync(first.Id)).Status.Should().Be(InvoiceStatus.Approved);
        (await LoadAsync(second.Id)).Status.Should().Be(InvoiceStatus.Declined);
    }

    private static IKafkaConsumerMessage<ApprovalResultKey, ApprovalResultValue> Message(long invoiceExternalId, ApprovalStatus status)
    {
        var value = new ApprovalResultValue { InvoiceId = invoiceExternalId, Status = status };

        return Mock.Of<IKafkaConsumerMessage<ApprovalResultKey, ApprovalResultValue>>(m => m.Value == value);
    }

    private async Task HandleAsync(params IKafkaConsumerMessage<ApprovalResultKey, ApprovalResultValue>[] messages)
    {
        await using AsyncServiceScope scope = Grpc.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ApprovalResultKafkaHandler>(scope.ServiceProvider);

        await handler.HandleAsync(messages, CancellationToken.None);
    }

    private async Task<(Invoice Invoice, long ExternalId)> SeedInvoiceAsync()
    {
        var invoice = new Invoice("payer-1", "payee-1", 10);
        long externalId = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).SaveAsync(invoice);

        return (invoice, externalId);
    }

    private async Task<Invoice> LoadAsync(Guid id)
    {
        Invoice? loaded = await new PostgresInvoiceRepository(Grpc.ConnectionProvider).GetByIdAsync(id);
        Assert.NotNull(loaded);
        return loaded;
    }
}
