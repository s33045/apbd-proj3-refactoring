using System;
using System.Collections.Generic;

namespace LegacyRenewalApp;

public class SubscriptionRenewalService
{
    private readonly IBillingGateway _billingGateway;
    private readonly ICustomerRepository _customerRepository;
    private readonly ISubscriptionPlanRepository _planRepository;

    public SubscriptionRenewalService() : this(new CustomerRepository(), new SubscriptionPlanRepository(),
        new LegacyBillingGatewayAdapter())
    {
    }

    public SubscriptionRenewalService(ICustomerRepository customerRepository,
        ISubscriptionPlanRepository planRepository, IBillingGateway billingGateway)
    {
        _customerRepository = customerRepository;
        _planRepository = planRepository;
        _billingGateway = billingGateway;
    }

    public RenewalInvoice CreateRenewalInvoice(
        int customerId,
        string planCode,
        int seatCount,
        string paymentMethod,
        bool includePremiumSupport,
        bool useLoyaltyPoints)
    {
        ValidateInput(customerId, planCode, seatCount, paymentMethod);

        var normalizedPlanCode = planCode.Trim().ToUpperInvariant();
        var normalizedPaymentMethod = paymentMethod.Trim().ToUpperInvariant();

        var customer = _customerRepository.GetById(customerId);
        var plan = _planRepository.GetByCode(normalizedPlanCode);

        if (!customer.IsActive) throw new InvalidOperationException("Inactive customers cannot renew subscriptions");

        var baseAmount = plan.MonthlyPricePerSeat * seatCount * 12m + plan.SetupFee;
        var notes = new List<string>();

        var discountAmount = CalculateDiscount(customer, plan, seatCount, baseAmount, useLoyaltyPoints, notes);

        var subtotalAfterDiscount = baseAmount - discountAmount;
        if (subtotalAfterDiscount < 300m)
        {
            subtotalAfterDiscount = 300m;
            AddNote(notes, "minimum discounted subtotal applied");
        }

        var supportFee = CalculateSupportFee(includePremiumSupport, normalizedPlanCode, notes);
        var paymentFee = CalculatePaymentFee(normalizedPaymentMethod, subtotalAfterDiscount + supportFee, notes);

        var taxRate = GetTaxRate(customer.Country);
        var taxBase = subtotalAfterDiscount + supportFee + paymentFee;
        var taxAmount = taxBase * taxRate;
        var finalAmount = taxBase + taxAmount;

        if (finalAmount < 500m)
        {
            finalAmount = 500m;
            AddNote(notes, "minimum invoice amount applied");
        }

        var invoice = BuildInvoice(
            customer,
            normalizedPlanCode,
            normalizedPaymentMethod,
            seatCount,
            baseAmount,
            discountAmount,
            supportFee,
            paymentFee,
            taxAmount,
            finalAmount,
            notes
        );

        _billingGateway.SaveInvoice(invoice);
        SendInvoiceEmail(customer, normalizedPlanCode, invoice);

        return invoice;
    }


    private static decimal CalculateDiscount(Customer customer, SubscriptionPlan plan, int seatCount,
        decimal baseAmount, bool useLoyaltyPoints, List<string> notes)
    {
        var discountAmount = 0m;

        discountAmount += CalculateSegmentDiscount(customer, plan, baseAmount, notes);
        discountAmount += CalculateYearsDiscount(customer, baseAmount, notes);
        discountAmount += CalculateSeatDiscount(seatCount, baseAmount, notes);

        if (!useLoyaltyPoints || customer.LoyaltyPoints <= 0) return discountAmount;
        var pointsToUse = customer.LoyaltyPoints > 200 ? 200 : customer.LoyaltyPoints;
        discountAmount += pointsToUse;
        AddNote(notes, $"loyalty points used: {pointsToUse}");

        return discountAmount;
    }

    private static decimal CalculateSegmentDiscount(Customer customer, SubscriptionPlan plan, decimal baseAmount,
        List<string> notes)
    {
        switch (customer.Segment)
        {
            case "Silver":
                AddNote(notes, "silver discount");
                return baseAmount * 0.05m;
            case "Gold":
                AddNote(notes, "gold discount");
                return baseAmount * 0.10m;
            case "Platinum":
                AddNote(notes, "platinum discount");
                return baseAmount * 0.15m;
            case "Education" when plan.IsEducationEligible:
                AddNote(notes, "education discount");
                return baseAmount * 0.20m;
            default:
                return 0m;
        }
    }

    private static decimal CalculateYearsDiscount(Customer customer, decimal baseAmount, List<string> notes)
    {
        switch (customer.YearsWithCompany)
        {
            case >= 5:
                AddNote(notes, "long-term loyalty discount");
                return baseAmount * 0.07m;
            case >= 2:
                AddNote(notes, "basic loyalty discount");
                return baseAmount * 0.03m;
            default:
                return 0m;
        }
    }

    private static decimal CalculateSeatDiscount(int seatCount, decimal baseAmount, List<string> notes)
    {
        switch (seatCount)
        {
            case >= 50:
                AddNote(notes, "large team discount");
                return baseAmount * 0.12m;
            case >= 20:
                AddNote(notes, "medium team discount");
                return baseAmount * 0.08m;
            case >= 10:
                AddNote(notes, "small team discount");
                return baseAmount * 0.04m;
            default:
                return 0m;
        }
    }

    private static decimal CalculateSupportFee(bool includePremiumSupport, string normalizedPlanCode,
        List<string> notes)
    {
        if (!includePremiumSupport) return 0m;
        AddNote(notes, "premium support included");
        return normalizedPlanCode switch
        {
            "START" => 250m,
            "PRO" => 400m,
            "ENTERPRISE" => 700m,
            _ => 0m
        };
    }

    private static decimal CalculatePaymentFee(string normalizedPaymentMethod, decimal amountBeforePaymentFee,
        List<string> notes)
    {
        switch (normalizedPaymentMethod)
        {
            case "CARD":
                AddNote(notes, "card payment fee");
                return amountBeforePaymentFee * 0.02m;
            case "BANK_TRANSFER":
                AddNote(notes, "bank transfer fee");
                return amountBeforePaymentFee * 0.01m;
            case "PAYPAL":
                AddNote(notes, "paypal fee");
                return amountBeforePaymentFee * 0.035m;
            case "INVOICE":
                AddNote(notes, "invoice payment");
                return 0m;
            default:
                throw new ArgumentException("Unsupported payment method");
        }
    }

    private static decimal GetTaxRate(string country)
    {
        return country switch
        {
            "Poland" => 0.23m,
            "Germany" => 0.19m,
            "Czech Republic" => 0.21m,
            "Norway" => 0.25m,
            _ => 0.20m
        };
    }


    private void SendInvoiceEmail(Customer customer, string normalizedPlanCode, RenewalInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(customer.Email)) return;

        var subject = "Subscription renewal invoice";
        var body =
            $"Hello {customer.FullName}, your renewal for plan {normalizedPlanCode} " +
            $"has been prepared. Final amount: {invoice.FinalAmount:F2}.";

        _billingGateway.SendEmail(customer.Email, subject, body);
    }

    private static RenewalInvoice BuildInvoice(
        Customer customer,
        string normalizedPlanCode,
        string normalizedPaymentMethod,
        int seatCount,
        decimal baseAmount,
        decimal discountAmount,
        decimal supportFee,
        decimal paymentFee,
        decimal taxAmount,
        decimal finalAmount,
        List<string> notes)
    {
        var generatedAt = DateTime.UtcNow;

        return new RenewalInvoice
        {
            InvoiceNumber = $"INV-{generatedAt:yyyyMMdd}-{customer.Id}-{normalizedPlanCode}",
            CustomerName = customer.FullName,
            PlanCode = normalizedPlanCode,
            PaymentMethod = normalizedPaymentMethod,
            SeatCount = seatCount,
            BaseAmount = RoundMoney(baseAmount),
            DiscountAmount = RoundMoney(discountAmount),
            SupportFee = RoundMoney(supportFee),
            PaymentFee = RoundMoney(paymentFee),
            TaxAmount = RoundMoney(taxAmount),
            FinalAmount = RoundMoney(finalAmount),
            Notes = FormatNotes(notes),
            GeneratedAt = generatedAt
        };
    }

    private static void ValidateInput(int customerId, string planCode, int seatCount, string paymentMethod)
    {
        if (customerId <= 0) throw new ArgumentException("Customer id must be positive");
        if (string.IsNullOrWhiteSpace(planCode)) throw new ArgumentException("Plan code is required");
        if (seatCount <= 0) throw new ArgumentException("Seat count must be positive");
        if (string.IsNullOrWhiteSpace(paymentMethod)) throw new ArgumentException("Payment method is required");
    }

    private static void AddNote(List<string> notes, string note)
    {
        notes.Add(note);
    }

    private static string FormatNotes(List<string> notes)
    {
        if (notes.Count == 0) return string.Empty;
        return string.Join("; ", notes) + ";";
    }

    private static decimal RoundMoney(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}