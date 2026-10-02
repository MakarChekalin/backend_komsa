namespace Atm.Domain;

public enum InvoiceStatus
{
    Created,
    Paid,
    Cancelled,
}

public class Invoice
{
    public Guid Id { get; }

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
        DateTime? createdAt = null)
    {
        Id = id ?? Guid.NewGuid();
        PayerAccountNumber = payerAccountNumber;
        PayeeAccountNumber = payeeAccountNumber;
        Amount = amount;
        Status = status;
        CreatedAt = createdAt ?? DateTime.UtcNow;
    }

    public bool CanBePaid => Status == InvoiceStatus.Created;

    public bool Pay()
    {
        if (!CanBePaid)
            return false;

        Status = InvoiceStatus.Paid;
        return true;
    }

    public bool Cancel()
    {
        if (Status != InvoiceStatus.Created)
            return false;

        Status = InvoiceStatus.Cancelled;
        return true;
    }
}
