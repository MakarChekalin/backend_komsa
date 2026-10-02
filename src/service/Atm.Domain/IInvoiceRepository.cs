namespace Atm.Domain;

public interface IInvoiceRepository
{
    Task<Invoice?> GetByIdAsync(Guid id);

    Task<Invoice?> GetByIdForUpdateAsync(Guid id);

    Task<Invoice?> GetByExternalIdAsync(long externalId);

    Task<Invoice?> GetByExternalIdForUpdateAsync(long externalId);

    Task<long> SaveAsync(Invoice invoice);

    IAsyncEnumerable<Invoice> GetOutgoingAsync(string payeeAccountNumber, string? payerAccountNumber, InvoiceStatus? status, Guid? cursor, int pageSize);

    IAsyncEnumerable<Invoice> GetIncomingAsync(string payerAccountNumber, string? payeeAccountNumber, InvoiceStatus? status, Guid? cursor, int pageSize);
}
