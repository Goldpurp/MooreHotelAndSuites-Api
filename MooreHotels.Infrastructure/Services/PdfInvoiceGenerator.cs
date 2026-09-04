using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Enums;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MooreHotels.Infrastructure.Services;

public class PdfInvoiceGenerator : IPdfInvoiceGenerator
{
    private readonly HotelSettings _settings;
    private readonly IHotelTimeService _hotelTime;

    static PdfInvoiceGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public PdfInvoiceGenerator(
        IOptions<HotelSettings> settings,
        IHotelTimeService hotelTime)
    {
        _settings = settings.Value;
        _hotelTime = hotelTime;
    }

    public byte[] GenerateInvoicePdf(BookingDto booking, IEnumerable<BookingAddOnDto>? addOns = null)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(35);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken3));

                page.Header().Element(header => ComposeHeader(header, booking));
                page.Content().Element(content => ComposeContent(content, booking, addOns));
                page.Footer().Element(ComposeFooter);
            });
        });

        return document.GeneratePdf();
    }

    private void ComposeHeader(IContainer container, BookingDto booking)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(_settings.Name.ToUpperInvariant()).Bold().FontSize(18).FontColor(Colors.Teal.Darken4);
                col.Item().Text(_settings.Tagline).FontSize(9).FontColor(Colors.Grey.Darken1);
                col.Item().Text(_settings.Address).FontSize(8).FontColor(Colors.Grey.Medium);
                col.Item().Text($"{_settings.SupportEmail} | {_settings.Phone}").FontSize(8).FontColor(Colors.Grey.Medium);
            });

            row.ConstantItem(180).Column(col =>
            {
                col.Item().AlignRight().Text("OFFICIAL FOLIO & INVOICE").Bold().FontSize(12).FontColor(Colors.Teal.Darken3);
                col.Item().AlignRight().Text($"Booking: {booking.BookingCode}").Bold().FontSize(10);
                col.Item().AlignRight().Text($"Issued: {_hotelTime.ToHotelLocalTime(DateTime.UtcNow):dd MMM yyyy}").FontSize(9).FontColor(Colors.Grey.Darken1);
                col.Item().AlignRight().Container().PaddingTop(4).Element(c =>
                {
                    var isPaid = booking.PaymentStatus == PaymentStatus.Paid;
                    c.Background(isPaid ? Colors.Green.Lighten4 : Colors.Amber.Lighten4)
                     .PaddingVertical(3)
                     .PaddingHorizontal(8)
                     .Text(isPaid ? "PAYMENT COMPLETED" : "PAYMENT PENDING")
                     .Bold().FontSize(8).FontColor(isPaid ? Colors.Green.Darken3 : Colors.Amber.Darken3);
                });
            });
        });
    }

    private void ComposeContent(IContainer container, BookingDto booking, IEnumerable<BookingAddOnDto>? addOns)
    {
        var addOnItems = addOns?.ToList() ?? [];
        var addOnTotal = addOnItems.Sum(item => item.TotalPrice);
        var roomSubtotal = Math.Max(0, booking.Amount - addOnTotal);
        var localCheckIn = _hotelTime.ToHotelLocalTime(booking.CheckIn);
        var localCheckOut = _hotelTime.ToHotelLocalTime(booking.CheckOut);
        var nights = Math.Max(1, (localCheckOut.Date - localCheckIn.Date).Days);

        container.PaddingVertical(16).Column(col =>
        {
            // Guest & Stay Info Table
            col.Item().Row(row =>
            {
                row.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(c =>
                {
                    c.Item().Text("GUEST DETAILS").Bold().FontSize(9).FontColor(Colors.Teal.Darken3);
                    c.Item().Text($"{booking.GuestFirstName} {booking.GuestLastName}").Bold().FontSize(11);
                    c.Item().Text($"Email: {booking.GuestEmail}").FontSize(9);
                    c.Item().Text($"Phone: {booking.GuestPhone}").FontSize(9);
                });

                row.ConstantItem(15);

                row.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(c =>
                {
                    c.Item().Text("STAY SUMMARY").Bold().FontSize(9).FontColor(Colors.Teal.Darken3);
                    c.Item().Text($"Folio Reference: {booking.BookingCode}").Bold().FontSize(10);
                    c.Item().Text($"Check-in: {localCheckIn:dd MMM yyyy} ({_hotelTime.CheckInTime:h\\:mm tt})").FontSize(9);
                    c.Item().Text($"Check-out: {localCheckOut:dd MMM yyyy} ({_hotelTime.CheckOutTime:h\\:mm tt})").FontSize(9);
                    c.Item().Text($"Duration: {nights} Night(s)").FontSize(9);
                });
            });

            col.Item().PaddingTop(18);

            // Charges Table
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                });

                table.Header(header =>
                {
                    header.Cell().Background(Colors.Teal.Darken4).Padding(6).Text("Item / Service Description").Bold().FontColor(Colors.White).FontSize(9);
                    header.Cell().Background(Colors.Teal.Darken4).Padding(6).AlignCenter().Text("Qty / Nights").Bold().FontColor(Colors.White).FontSize(9);
                    header.Cell().Background(Colors.Teal.Darken4).Padding(6).AlignRight().Text("Rate (NGN)").Bold().FontColor(Colors.White).FontSize(9);
                    header.Cell().Background(Colors.Teal.Darken4).Padding(6).AlignRight().Text("Amount (NGN)").Bold().FontColor(Colors.White).FontSize(9);
                });

                var nightlyRate = roomSubtotal / nights;

                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).Text($"Room Accommodation - Reservation #{booking.BookingCode}").Bold();
                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).AlignCenter().Text($"{nights}");
                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).AlignRight().Text($"{nightlyRate:N2}");
                table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).AlignRight().Text($"{roomSubtotal:N2}");

                foreach (var addon in addOnItems)
                {
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).Text($"Add-on: {addon.ServiceName} ({addon.Category})");
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).AlignCenter().Text($"{addon.Quantity}");
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).AlignRight().Text($"{addon.UnitPrice:N2}");
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(6).AlignRight().Text($"{addon.TotalPrice:N2}");
                }
            });

            col.Item().PaddingTop(12);

            // Total Block
            col.Item().AlignRight().Width(240).Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Room subtotal:").FontSize(9);
                    r.RelativeItem().AlignRight().Text($"NGN {roomSubtotal:N2}").FontSize(9);
                });
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Add-ons:").FontSize(9);
                    r.RelativeItem().AlignRight().Text($"NGN {addOnTotal:N2}").FontSize(9);
                });
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Total Amount:").Bold().FontSize(11);
                    r.RelativeItem().AlignRight().Text($"NGN {booking.Amount:N2}").Bold().FontSize(12).FontColor(Colors.Teal.Darken4);
                });
                c.Item().PaddingTop(2).Row(r =>
                {
                    r.RelativeItem().Text("Payment Method:").FontSize(8).FontColor(Colors.Grey.Medium);
                    r.RelativeItem().AlignRight().Text(booking.PaymentMethod?.ToString() ?? "Direct Transfer").FontSize(8).FontColor(Colors.Grey.Darken2);
                });
                if (!string.IsNullOrWhiteSpace(booking.TransactionReference))
                {
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Ref:").FontSize(8).FontColor(Colors.Grey.Medium);
                        r.RelativeItem().AlignRight().Text(booking.TransactionReference).FontSize(8).FontColor(Colors.Grey.Darken2);
                    });
                }
            });
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(8).Row(row =>
        {
            row.RelativeItem().Text($"Thank you for choosing {_settings.Name}. We wish you a delightful stay.").FontSize(8).FontColor(Colors.Grey.Medium);
            row.RelativeItem().AlignRight().Text(x =>
            {
                x.Span("Page ");
                x.CurrentPageNumber();
                x.Span(" of ");
                x.TotalPages();
            });
        });
    }
}
