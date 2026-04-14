using System;

namespace LegacyRenewalApp;

public class SubscriptionRenewalService
{
    public RenewalInvoice CreateRenewalInvoice(
        int customerId,
        string planCode,
        int seatCount,
        string paymentMethod,
        bool includePremiumSupport,
        bool useLoyaltyPoints)
    {
        if (customerId <= 0) throw new ArgumentException("Customer id must be positive");

        if (string.IsNullOrWhiteSpace(planCode)) throw new ArgumentException("Plan code is required");

        if (seatCount <= 0) throw new ArgumentException("Seat count must be positive");

        if (string.IsNullOrWhiteSpace(paymentMethod)) throw new ArgumentException("Payment method is required");

        var normalizedPlanCode = planCode.Trim().ToUpperInvariant();
        var normalizedPaymentMethod = paymentMethod.Trim().ToUpperInvariant();

        var customerRepository = new CustomerRepository();
        var planRepository = new SubscriptionPlanRepository();

        var customer = customerRepository.GetById(customerId);
        var plan = planRepository.GetByCode(normalizedPlanCode);

        if (!customer.IsActive) throw new InvalidOperationException("Inactive customers cannot renew subscriptions");

        var baseAmount = plan.MonthlyPricePerSeat * seatCount * 12m + plan.SetupFee;
        var discountAmount = 0m;
        var notes = string.Empty;

        if (customer.Segment == "Silver")
        {
            discountAmount += baseAmount * 0.05m;
            notes += "silver discount; ";
        }
        else if (customer.Segment == "Gold")
        {
            discountAmount += baseAmount * 0.10m;
            notes += "gold discount; ";
        }
        else if (customer.Segment == "Platinum")
        {
            discountAmount += baseAmount * 0.15m;
            notes += "platinum discount; ";
        }
        else if (customer.Segment == "Education" && plan.IsEducationEligible)
        {
            discountAmount += baseAmount * 0.20m;
            notes += "education discount; ";
        }

        if (customer.YearsWithCompany >= 5)
        {
            discountAmount += baseAmount * 0.07m;
            notes += "long-term loyalty discount; ";
        }
        else if (customer.YearsWithCompany >= 2)
        {
            discountAmount += baseAmount * 0.03m;
            notes += "basic loyalty discount; ";
        }

        if (seatCount >= 50)
        {
            discountAmount += baseAmount * 0.12m;
            notes += "large team discount; ";
        }
        else if (seatCount >= 20)
        {
            discountAmount += baseAmount * 0.08m;
            notes += "medium team discount; ";
        }
        else if (seatCount >= 10)
        {
            discountAmount += baseAmount * 0.04m;
            notes += "small team discount; ";
        }

        if (useLoyaltyPoints && customer.LoyaltyPoints > 0)
        {
            var pointsToUse = customer.LoyaltyPoints > 200 ? 200 : customer.LoyaltyPoints;
            discountAmount += pointsToUse;
            notes += $"loyalty points used: {pointsToUse}; ";
        }

        var subtotalAfterDiscount = baseAmount - discountAmount;
        if (subtotalAfterDiscount < 300m)
        {
            subtotalAfterDiscount = 300m;
            notes += "minimum discounted subtotal applied; ";
        }

        var supportFee = 0m;
        if (includePremiumSupport)
        {
            if (normalizedPlanCode == "START")
                supportFee = 250m;
            else if (normalizedPlanCode == "PRO")
                supportFee = 400m;
            else if (normalizedPlanCode == "ENTERPRISE") supportFee = 700m;

            notes += "premium support included; ";
        }

        var paymentFee = 0m;
        if (normalizedPaymentMethod == "CARD")
        {
            paymentFee = (subtotalAfterDiscount + supportFee) * 0.02m;
            notes += "card payment fee; ";
        }
        else if (normalizedPaymentMethod == "BANK_TRANSFER")
        {
            paymentFee = (subtotalAfterDiscount + supportFee) * 0.01m;
            notes += "bank transfer fee; ";
        }
        else if (normalizedPaymentMethod == "PAYPAL")
        {
            paymentFee = (subtotalAfterDiscount + supportFee) * 0.035m;
            notes += "paypal fee; ";
        }
        else if (normalizedPaymentMethod == "INVOICE")
        {
            paymentFee = 0m;
            notes += "invoice payment; ";
        }
        else
        {
            throw new ArgumentException("Unsupported payment method");
        }

        var taxRate = 0.20m;
        if (customer.Country == "Poland")
            taxRate = 0.23m;
        else if (customer.Country == "Germany")
            taxRate = 0.19m;
        else if (customer.Country == "Czech Republic")
            taxRate = 0.21m;
        else if (customer.Country == "Norway") taxRate = 0.25m;

        var taxBase = subtotalAfterDiscount + supportFee + paymentFee;
        var taxAmount = taxBase * taxRate;
        var finalAmount = taxBase + taxAmount;

        if (finalAmount < 500m)
        {
            finalAmount = 500m;
            notes += "minimum invoice amount applied; ";
        }

        var invoice = new RenewalInvoice
        {
            InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{customerId}-{normalizedPlanCode}",
            CustomerName = customer.FullName,
            PlanCode = normalizedPlanCode,
            PaymentMethod = normalizedPaymentMethod,
            SeatCount = seatCount,
            BaseAmount = Math.Round(baseAmount, 2, MidpointRounding.AwayFromZero),
            DiscountAmount = Math.Round(discountAmount, 2, MidpointRounding.AwayFromZero),
            SupportFee = Math.Round(supportFee, 2, MidpointRounding.AwayFromZero),
            PaymentFee = Math.Round(paymentFee, 2, MidpointRounding.AwayFromZero),
            TaxAmount = Math.Round(taxAmount, 2, MidpointRounding.AwayFromZero),
            FinalAmount = Math.Round(finalAmount, 2, MidpointRounding.AwayFromZero),
            Notes = notes.Trim(),
            GeneratedAt = DateTime.UtcNow
        };

        LegacyBillingGateway.SaveInvoice(invoice);

        if (!string.IsNullOrWhiteSpace(customer.Email))
        {
            var subject = "Subscription renewal invoice";
            var body =
                $"Hello {customer.FullName}, your renewal for plan {normalizedPlanCode} " +
                $"has been prepared. Final amount: {invoice.FinalAmount:F2}.";

            LegacyBillingGateway.SendEmail(customer.Email, subject, body);
        }

        return invoice;
    }
}