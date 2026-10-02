using ApprovalResult.Kafka.Contracts;
using Atm.Domain;
using Itmo.Dev.Platform.Kafka.Consumer;
using Itmo.Dev.Platform.Persistence.Abstractions.Transactions;
using System.Data;

namespace Atm.Grpc.Kafka;

public class ApprovalResultKafkaHandler : IKafkaConsumerHandler<ApprovalResultKey, ApprovalResultValue>
{
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IPersistenceTransactionProvider _transactionProvider;
    private readonly ILogger<ApprovalResultKafkaHandler> _logger;

    public ApprovalResultKafkaHandler(
        IInvoiceRepository invoiceRepository,
        IPersistenceTransactionProvider transactionProvider,
        ILogger<ApprovalResultKafkaHandler> logger)
    {
        _invoiceRepository = invoiceRepository;
        _transactionProvider = transactionProvider;
        _logger = logger;
    }

    public async ValueTask HandleAsync(
        IEnumerable<IKafkaConsumerMessage<ApprovalResultKey, ApprovalResultValue>> messages,
        CancellationToken cancellationToken)
    {
        foreach (IKafkaConsumerMessage<ApprovalResultKey, ApprovalResultValue> message in messages)
            await HandleAsync(message.Value, cancellationToken);
    }

    private async Task HandleAsync(ApprovalResultValue value, CancellationToken cancellationToken)
    {
        await using IPersistenceTransaction transaction = await _transactionProvider.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        Atm.Domain.Invoice? invoice = await _invoiceRepository.GetByExternalIdForUpdateAsync(value.InvoiceId);

        if (invoice == null)
        {
            _logger.LogWarning("Approval result for unknown invoice {InvoiceExternalId} ignored", value.InvoiceId);
            return;
        }

        bool applied = value.Status switch
        {
            ApprovalStatus.Approved => invoice.Approve(),
            ApprovalStatus.Declined => invoice.Decline(),
            ApprovalStatus.Unspecified => false,
            _ => false,
        };

        if (!applied)
        {
            _logger.LogWarning(
                "Approval result {Status} for invoice {InvoiceExternalId} could not be applied from state {State}",
                value.Status,
                value.InvoiceId,
                invoice.Status);

            return;
        }

        await _invoiceRepository.SaveAsync(invoice);

        await transaction.CommitAsync(cancellationToken);
    }
}
