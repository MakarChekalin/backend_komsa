using Atm.Application;
using System.Collections.Concurrent;

namespace Atm.IntegrationTests;

public sealed class FakeInvoiceApprovalClient : IInvoiceApprovalClient
{
    private readonly ConcurrentQueue<ApprovalCall> _calls = new();

    public InvoiceApprovalCallResult Result { get; set; } = InvoiceApprovalCallResult.Success;

    public IReadOnlyCollection<ApprovalCall> Calls => _calls.ToArray();

    public void Reset()
    {
        Result = InvoiceApprovalCallResult.Success;
        _calls.Clear();
    }

    public Task<InvoiceApprovalCallResult> AssignAccountantAsync(long invoiceExternalId, long accountantExternalId, CancellationToken cancellationToken)
    {
        return Record("assign", invoiceExternalId, accountantExternalId);
    }

    public Task<InvoiceApprovalCallResult> ApproveInvoiceAsync(long invoiceExternalId, long accountantExternalId, CancellationToken cancellationToken)
    {
        return Record("approve", invoiceExternalId, accountantExternalId);
    }

    public Task<InvoiceApprovalCallResult> DeclineInvoiceAsync(long invoiceExternalId, long accountantExternalId, CancellationToken cancellationToken)
    {
        return Record("decline", invoiceExternalId, accountantExternalId);
    }

    private Task<InvoiceApprovalCallResult> Record(string operation, long invoiceExternalId, long accountantExternalId)
    {
        _calls.Enqueue(new ApprovalCall(operation, invoiceExternalId, accountantExternalId));
        return Task.FromResult(Result);
    }

    public sealed record ApprovalCall(string Operation, long InvoiceExternalId, long AccountantExternalId);
}
