namespace Atm.Domain;

public enum InvoiceStatus
{
    Created,
    Paid,
    Cancelled,
    Approved,
    Declined,
}

public class Invoice
{
    public Guid Id { get; }

    public long ExternalId { get; }

    public string PayerAccountNumber { get; }

    public string PayeeAccountNumber { get; }

    public decimal Amount { get; }

    public InvoiceStatus Status { get; private set; }

    public DateTime CreatedAt { get; }

    public Invoice(
        string payerAccountNumber,
        string payeeAccountNumber,
        decimal amount,
        Guid? id = null,
        InvoiceStatus status = InvoiceStatus.Created,
        DateTime? createdAt = null,
        long externalId = 0)
    {
        Id = id ?? Guid.NewGuid();
        PayerAccountNumber = payerAccountNumber;
        PayeeAccountNumber = payeeAccountNumber;
        Amount = amount;
        Status = status;
        CreatedAt = createdAt ?? DateTime.UtcNow;
        ExternalId = externalId;
    }

    public bool CanBePaid => Status is InvoiceStatus.Created or InvoiceStatus.Approved;

    public bool Pay()
    {
        if (!CanBePaid)
            return false;

        Status = InvoiceStatus.Paid;
        return true;
    }

    public bool Cancel()
    {
        if (Status != InvoiceStatus.Created && Status != InvoiceStatus.Approved)
            return false;

        Status = InvoiceStatus.Cancelled;
        return true;
    }

    public bool Approve()
    {
        if (Status != InvoiceStatus.Created)
            return false;

        Status = InvoiceStatus.Approved;
        return true;
    }

    public bool Decline()
    {
        if (Status != InvoiceStatus.Created)
            return false;

        Status = InvoiceStatus.Declined;
        return true;
    }
}
