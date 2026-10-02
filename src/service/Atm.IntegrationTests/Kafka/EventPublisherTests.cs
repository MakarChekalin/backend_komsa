using Accounts.Kafka.Contracts;
using Atm.Grpc.Kafka;
using FluentAssertions;
using Invoices.Kafka.Contracts;
using Itmo.Dev.Platform.Kafka.Producer;
using Moq;

namespace Atm.IntegrationTests.Kafka;

public class EventPublisherTests
{
    [Theory]
    [InlineData(Atm.Domain.AccountType.Personal, Accounts.Kafka.Contracts.AccountType.Personal)]
    [InlineData(Atm.Domain.AccountType.Corporate, Accounts.Kafka.Contracts.AccountType.Corporate)]
    public async Task AccountEventPublisher_ShouldProduceMessageWithAllFieldsAndMappedType(
        Atm.Domain.AccountType type,
        Accounts.Kafka.Contracts.AccountType expectedType)
    {
        var captured = new List<KafkaProducerMessage<AccountCreationKey, AccountCreationValue>>();
        var producer = new Mock<IKafkaMessageProducer<AccountCreationKey, AccountCreationValue>>();

        producer
            .Setup(p => p.ProduceAsync(It.IsAny<IAsyncEnumerable<KafkaProducerMessage<AccountCreationKey, AccountCreationValue>>>(), It.IsAny<CancellationToken>()))
            .Callback<IAsyncEnumerable<KafkaProducerMessage<AccountCreationKey, AccountCreationValue>>, CancellationToken>(
                (messages, _) => captured.AddRange(messages.ToBlockingEnumerable()));

        var publisher = new AccountEventPublisher(producer.Object);

        await publisher.PublishAccountCreatedAsync(ownerExternalId: 5, accountExternalId: 77, type, CancellationToken.None);

        KafkaProducerMessage<AccountCreationKey, AccountCreationValue> message = captured.Should().ContainSingle().Subject;
        message.Key.AccountId.Should().Be(77);
        message.Value.AccountId.Should().Be(77);
        message.Value.UserId.Should().Be(5);
        message.Value.AccountType.Should().Be(expectedType);
    }

    [Fact]
    public async Task InvoiceEventPublisher_ShouldProduceMessageWithIdsAndMoney()
    {
        var captured = new List<KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue>>();
        var producer = new Mock<IKafkaMessageProducer<InvoiceCreationKey, InvoiceCreationValue>>();

        producer
            .Setup(p => p.ProduceAsync(It.IsAny<IAsyncEnumerable<KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue>>>(), It.IsAny<CancellationToken>()))
            .Callback<IAsyncEnumerable<KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue>>, CancellationToken>(
                (messages, _) => captured.AddRange(messages.ToBlockingEnumerable()));

        var publisher = new InvoiceEventPublisher(producer.Object);

        await publisher.PublishInvoiceCreatedAsync(
            invoiceExternalId: 10,
            payeeExternalId: 20,
            payerExternalId: 30,
            amount: 123.45m,
            CancellationToken.None);

        KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue> message = captured.Should().ContainSingle().Subject;
        message.Key.InvoiceId.Should().Be(10);
        message.Value.InvoiceId.Should().Be(10);
        message.Value.RecipientId.Should().Be(20);
        message.Value.PayerId.Should().Be(30);
        message.Value.Payment.CurrencyCode.Should().Be("RUB");
        message.Value.Payment.Units.Should().Be(123);
        message.Value.Payment.Nanos.Should().Be(450_000_000);
    }

    [Theory]
    [InlineData("0", 0, 0)]
    [InlineData("1", 1, 0)]
    [InlineData("0.01", 0, 10_000_000)]
    [InlineData("250.75", 250, 750_000_000)]
    [InlineData("1000000.5", 1_000_000, 500_000_000)]
    public async Task InvoiceEventPublisher_ShouldSplitAmountIntoUnitsAndNanos(string amountText, long expectedUnits, int expectedNanos)
    {
        decimal amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var captured = new List<KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue>>();
        var producer = new Mock<IKafkaMessageProducer<InvoiceCreationKey, InvoiceCreationValue>>();

        producer
            .Setup(p => p.ProduceAsync(It.IsAny<IAsyncEnumerable<KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue>>>(), It.IsAny<CancellationToken>()))
            .Callback<IAsyncEnumerable<KafkaProducerMessage<InvoiceCreationKey, InvoiceCreationValue>>, CancellationToken>(
                (messages, _) => captured.AddRange(messages.ToBlockingEnumerable()));

        await new InvoiceEventPublisher(producer.Object).PublishInvoiceCreatedAsync(1, 2, 3, amount, CancellationToken.None);

        captured.Single().Value.Payment.Units.Should().Be(expectedUnits);
        captured.Single().Value.Payment.Nanos.Should().Be(expectedNanos);
    }
}
